using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;

namespace EProxyServer.Net
{
    class TunnelClient
    {
        private const byte Key = 0x53;
        private const UInt32 KeyLarge32 = 0x53535353;
        private const UInt64 KeyLarge64 = 0x5353535353535353;

        private bool Disposed = false;
        private Socket Client;
        private SocketAsyncEventArgs SendArgs;
        private SocketAsyncEventArgs ReceiveArgs;
        private MemoryStream InputStream = new MemoryStream();
        private MemoryStream OutputStream = new MemoryStream();
        private int OutstandingSends = 1;

        public Dictionary<short, Destination> Destinations = new Dictionary<short,Destination>();

        /// <summary>
        /// Crypts data.
        /// </summary>
        private static Action<byte[]> Crypt;

        static TunnelClient()
        {
            if (Environment.Is64BitProcess)
                Crypt = Crypt64;
            else
                Crypt = Crypt32;
        }

        public TunnelClient(Socket client)
        {
            Client = client;

            SendArgs = TunnelServer.Instance.PopArgs();
            ReceiveArgs = TunnelServer.Instance.PopArgs();

            SendArgs.Completed += Send_Completed;
            ReceiveArgs.Completed += Receive_Completed;
            ReceiveArgs.SetBuffer(new byte[1500], 0, 1500);

            if (!Client.ReceiveAsync(ReceiveArgs))
            {
                Receive_Completed(Client, ReceiveArgs);
            }
        }

        private void Send_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (Client == null)
                return;
            //Console.WriteLine("Tunnel sent {0} bytes.", e.BytesTransferred);
            Interlocked.Exchange(ref OutstandingSends, 1);
            ProcessOutput();
        }

        private void Receive_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (Client == null)
                return;
            //Console.WriteLine("Tunnel received {0} bytes.", e.BytesTransferred);
            if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
            {
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
                    Console.WriteLine("Tunnel disconnected from {0}.", Client.RemoteEndPoint);
                }
                catch
                {
                    Console.WriteLine("Tunnel disconnected.");
                }
                Dispose();
            }
        }

        private void ProcessInput()
        {
            lock (InputStream)
            {
                while (true)
                {
                    // need encryption here
                    // short length, byte[] encrypted data
                    if (InputStream.Length - InputStream.Position >= 2)
                    {
                        long pos = InputStream.Position;

                        byte[] buffer = new byte[2];
                        InputStream.Read(buffer, 0, buffer.Length);
                        short length = BitConverter.ToInt16(buffer, 0);
                        if (InputStream.Length - InputStream.Position >= length)
                        {
                            buffer = new byte[length];
                            InputStream.Read(buffer, 0, buffer.Length);
                            Decrypt(buffer);

                            short id = BitConverter.ToInt16(buffer, 0);

                            if (Destinations.ContainsKey(id))
                            {
                                // standard tunnel
                                Destinations[id].Send(buffer, 2, length - 2);
                            }
                            else
                            {
                                // new connection
                                short port = BitConverter.ToInt16(buffer, 2);
                                length = BitConverter.ToInt16(buffer, 4);
                                if (length == buffer.Length - 6 && length  < 128)
                                {
                                    string host = UTF8Encoding.UTF8.GetString(buffer, 6, length);

                                    Destinations.Add(id, new Destination(this, id, host, port));
                                }
                                // otherwise invalid (probably disposed connection)
                            }
                        }
                        else
                        {
                            // not enough for packet
                            InputStream.Position = pos;
                            break;
                        }
                    }
                    else
                    {
                        if (InputStream.Position == InputStream.Length)
                        {
                            InputStream.SetLength(0);
                        }

                        // not enough for packet length
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Tunnels data to proxy client.
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

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                if (disposing)
                {
                    if (Client != null)
                    {
                        Client.Dispose();
                        Client = null;
                    }

                    if (SendArgs != null)
                    {
                        SendArgs.Completed -= Send_Completed;
                        TunnelServer.Instance.PushArgs(SendArgs);
                        SendArgs = null;
                    }

                    if (ReceiveArgs != null)
                    {
                        ReceiveArgs.Completed -= Receive_Completed;
                        TunnelServer.Instance.PushArgs(ReceiveArgs);
                        ReceiveArgs = null;
                    }

                    if (InputStream != null)
                    {
                        InputStream.Dispose();
                        InputStream = null;
                    }

                    if (OutputStream != null)
                    {
                        OutputStream.Dispose();
                        OutputStream = null;
                    }

                    if (Destinations != null)
                    {
                        Destinations.Values.ToList().ForEach(x => x.Dispose());
                        Destinations = null;
                    }
                }

                Disposed = true;
            }
        }
    }
}
