using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.IO;
using System.Net;
using System.Threading;

namespace EProxyClient.Net
{
    class Tunnel
    {
        private const byte Key = 0x53;
        private const UInt32 KeyLarge32 = 0x53535353;
        private const UInt64 KeyLarge64 = 0x5353535353535353;

        private Socket Client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private SocketAsyncEventArgs SendArgs = new SocketAsyncEventArgs();
        private SocketAsyncEventArgs ReceiveArgs = new SocketAsyncEventArgs();
        private MemoryStream InputStream = new MemoryStream();
        private MemoryStream OutputStream = new MemoryStream();
        private int OutstandingSends = 1;
        private bool Proxy = false;
        private bool ProxyInitialized = false;

        /// <summary>
        /// Crypts data.
        /// </summary>
        private static Action<byte[]> Crypt;

        private string Host = "skeerhouse.net";
        private int Port = 443;

        static Tunnel()
        {
            if (Environment.Is64BitProcess)
                Crypt = Crypt64;
            else
                Crypt = Crypt32;
        }

        public Tunnel(string pHost, int pPort)
        {
            Proxy = true;
            IPAddress address;
            if (!IPAddress.TryParse(pHost, out address))
            {
                address = Dns.GetHostAddresses(pHost).First(x => x.AddressFamily == AddressFamily.InterNetwork);
            }

            IPEndPoint endPoint = new IPEndPoint(address, pPort);

            Client.Connect(endPoint);
            Console.WriteLine("Connected to proxy at {0}.", endPoint);

            ReceiveArgs.SetBuffer(new byte[1500], 0, 1500);

            ReceiveArgs.Completed += Receive_Completed;
            SendArgs.Completed += Send_Completed;

            if (!Client.ReceiveAsync(ReceiveArgs))
            {
                Receive_Completed(Client, ReceiveArgs);
            }

            SendProxyHello();
        }

        private void SendProxyHello()
        {
            // CONNECT host:port HTTP/1.1\r\n
            // Host: host:port\r\n
            // \r\n
            byte[] buffer = UTF8Encoding.UTF8.GetBytes(string.Format("CONNECT {0}:{1} HTTP/1.1\r\nHost: {0}:{1}\r\n\r\n", Host, Port));

            lock (OutputStream)
            {
                long pos = OutputStream.Position;
                OutputStream.Position = OutputStream.Length;
                OutputStream.Write(buffer, 0, buffer.Length);
                OutputStream.Position = pos;
            }

            ProcessOutput();
        }

        public Tunnel()
        {
            IPAddress address;
            if (!IPAddress.TryParse(Host, out address))
            {
                address = Dns.GetHostAddresses(Host).First(x => x.AddressFamily == AddressFamily.InterNetwork);
            }

            IPEndPoint endPoint = new IPEndPoint(address, Port);

            Client.Connect(endPoint);
            Console.WriteLine("Connected to tunnel at {0}.", endPoint);

            ReceiveArgs.SetBuffer(new byte[1500], 0, 1500);

            ReceiveArgs.Completed += Receive_Completed;
            SendArgs.Completed += Send_Completed;

            if (!Client.ReceiveAsync(ReceiveArgs))
            {
                Receive_Completed(Client, ReceiveArgs);
            }
        }

        private void Send_Completed(object sender, SocketAsyncEventArgs e)
        {
            //Console.WriteLine("Tunnel sent {0} bytes.", e.BytesTransferred);
            Interlocked.Exchange(ref OutstandingSends, 1);
            ProcessOutput();
        }

        private void Receive_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
            {
                //Console.WriteLine("Tunnel received {0} bytes.", e.BytesTransferred);
                lock (InputStream)
                {
                    long pos = InputStream.Position;
                    InputStream.Position = InputStream.Length;
                    InputStream.Write(e.Buffer, 0, e.BytesTransferred);
                    InputStream.Position = pos;

                }
                ProcessInput();

                if (!Client.ReceiveAsync(ReceiveArgs))
                {
                    Receive_Completed(Client, ReceiveArgs);
                }
            }
            else
            {
                try
                {
                    if (Proxy)
                        Console.WriteLine("HTTP proxy disconnected from {0}.", Client.RemoteEndPoint);
                    else
                        Console.WriteLine("Tunnel disconnected from {0}.", Client.RemoteEndPoint);
                }
                catch
                {
                    if (Proxy)
                        Console.WriteLine("HTTP proxy disconnected.");
                    else
                        Console.WriteLine("Tunnel disconnected.");
                }
                // Dispose();
                // Tunnel must not disconnect
            }
        }

        /// <summary>
        /// Tunnels data to proxy server.
        /// </summary>
        /// <param name="id">ID of the destination.</param>
        /// <param name="buffer">The data to send.</param>
        public void Send(short id, byte[] buffer)
        {
            byte[] packet = new byte[buffer.Length + 2];
            Buffer.BlockCopy(BitConverter.GetBytes(id), 0, packet, 0, 2);
            Buffer.BlockCopy(buffer, 0, packet, 2, buffer.Length);

            //buffer = BitConverter.GetBytes(id).Concat(buffer).ToArray(); // to slow
            //Encrypt(buffer);
            Encrypt(packet);

            buffer = new byte[packet.Length + 2];
            Buffer.BlockCopy(BitConverter.GetBytes((short)packet.Length), 0, buffer, 0, 2);
            Buffer.BlockCopy(packet, 0, buffer, 2, packet.Length);

            //buffer = BitConverter.GetBytes((short)buffer.Length).Concat(buffer).ToArray(); // to slow

            lock (OutputStream)
            {
                long pos = OutputStream.Position;
                OutputStream.Position = OutputStream.Length;
                OutputStream.Write(buffer, 0, buffer.Length);
                OutputStream.Position = pos;
            }

            ProcessOutput();
        }

        private void ProcessOutput()
        {
            if (OutputStream.Length == OutputStream.Position || Interlocked.Exchange(ref OutstandingSends, 0) != 1)
                return;

            lock (OutputStream)
            {
                byte[] buffer = new byte[OutputStream.Length - OutputStream.Position];
                OutputStream.Read(buffer, 0, buffer.Length);
                OutputStream.SetLength(0);
                SendArgs.SetBuffer(buffer, 0, buffer.Length);
            }

            if (!Client.SendAsync(SendArgs))
            {
                Send_Completed(Client, SendArgs);
            }
        }

        private static void Encrypt(byte[] buffer)
        {
            Crypt(buffer);
        }

        private static void Decrypt(byte[] buffer)
        {
            Crypt(buffer);
        }

        /*private void Crypt(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; ++i)
            {
                buffer[i] ^= Key;
            }
        }*/

        /// <summary>
        /// Crypts buffer for 32-bit applications.
        /// </summary>
        /// <param name="buffer">The data to crypt.</param>
        private static void Crypt32(byte[] buffer)
        {
            int large = buffer.Length >> 2; // number of 4 byte chunks (drop last 2 bits aka / 4)
            int left = buffer.Length & 3; // remaining bytes (logical AND of last 2 bits aka % 4)
            unsafe
            {
                fixed (byte* bp = &buffer[0])
                {
                    UInt32* p = (UInt32*)bp; // retreives 4 bytes from bp
                    while (--large >= 0)
                    {
                        *p ^= KeyLarge32; // xor 4 bytes
                        ++p; // retrives next 4 bytes from bp
                    }

                    // p is left with 0 - 3 bytes
                    byte* bb = (byte*)p; // retrives 1 byte from p
                    while (--left >= 0)
                    {
                        *bb ^= Key; // xor 1 byte
                        ++bb; // retrieves next 1 byte from p
                    }
                }
            }
        }

        /// <summary>
        /// Crypts buffer for 64-bit applications.
        /// </summary>
        /// <param name="buffer">The data to crypt.</param>
        private static void Crypt64(byte[] buffer)
        {
            int large = buffer.Length >> 3;
            int left = buffer.Length & 7;
            unsafe
            {
                fixed (byte* bp = &buffer[0])
                {
                    UInt64* p = (UInt64*)bp;
                    while (--large >= 0)
                    {
                        *p ^= KeyLarge64;
                        ++p;
                    }
                    byte* bb = (byte*)p;
                    while (--left >= 0)
                    {
                        *bb ^= Key;
                        ++bb;
                    }
                }
            }
        }

        private void ProcessInput()
        {
            lock (InputStream)
            {
                while (true)
                {
                    if (Proxy && !ProxyInitialized)
                    {
                        // using proxy but not initialized
                        if (InputStream.Length > InputStream.Position)
                        {
                            long pos = InputStream.Position;
                            byte[] buffer = new byte[InputStream.Length - InputStream.Position];
                            InputStream.Read(buffer, 0, buffer.Length);
                            string response = UTF8Encoding.UTF8.GetString(buffer);
                            int length = response.IndexOf("\r\n\r\n");
                            if (length != -1) // found
                            {
                                length += 4;
                                InputStream.Position = pos + length;
                                if (response.Contains("Connection established"))
                                {
                                    Console.WriteLine("Connection established.");
                                    ProxyInitialized = true;
                                }
                                else
                                {
                                    Console.WriteLine("Connection failed:");
                                    Console.Write(response.Substring(0, length));
                                    return;
                                }
                            }
                            else
                            {
                                // incomplete packet
                                InputStream.Position = pos;
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        // proxy initialized or standard connection; doesnt matter
                        if (InputStream.Length - InputStream.Position >= 2)
                        {
                            long pos = InputStream.Position;
                            byte[] buffer = new byte[2];
                            InputStream.Read(buffer, 0, 2);
                            short length = BitConverter.ToInt16(buffer, 0);

                            if (InputStream.Length - InputStream.Position >= length)
                            {
                                buffer = new byte[length];
                                InputStream.Read(buffer, 0, buffer.Length);

                                Decrypt(buffer);

                                short id = BitConverter.ToInt16(buffer, 0);

                                if (SocksServer.Instance.Clients.ContainsKey(id) && SocksServer.Instance.Clients[id] != null)
                                {
                                    SocksServer.Instance.Clients[id].Send(buffer, 2, buffer.Length - 2);
                                }
                            }
                            else
                            {
                                InputStream.Position = pos;
                                break;
                            }
                        }
                        else
                        {
                            if (InputStream.Length == InputStream.Position)
                            {
                                InputStream.SetLength(0);
                            }
                            break;
                        }
                    }
                }
            }
        }

        public void SendDisconnect(short id)
        {
            byte[] packet = BitConverter.GetBytes(id);
            Encrypt(packet);
            byte[] buffer = new byte[packet.Length + 2];
            Buffer.BlockCopy(BitConverter.GetBytes((short)packet.Length), 0, buffer, 0, 2);
            Buffer.BlockCopy(packet, 0, buffer, 2, packet.Length);

            lock (OutputStream)
            {
                long pos = OutputStream.Position;
                OutputStream.Position = OutputStream.Length;
                OutputStream.Write(buffer, 0, buffer.Length);
                OutputStream.Position = pos;
            }

            ProcessOutput();
        }
    }
}
