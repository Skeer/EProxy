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
        private byte Key = 0x53;
        private Socket Client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private SocketAsyncEventArgs SendArgs = new SocketAsyncEventArgs();
        private SocketAsyncEventArgs ReceiveArgs = new SocketAsyncEventArgs();
        private MemoryStream InputStream = new MemoryStream();
        private MemoryStream OutputStream = new MemoryStream();
        private int OutstandingSends = 1;

        private string Host = "localhost";
        private int Port = 8125;

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
                    Console.WriteLine("Tunnel disconnected from {0}.", Client.RemoteEndPoint);
                }
                catch
                {
                    Console.WriteLine("Tunnel disconnected.");
                }
                // Dispose();
                // Tunnel must not disconnect
            }
        }

        public void Send(short id, byte[] buffer)
        {
            buffer = BitConverter.GetBytes(id).Concat(buffer).ToArray();
            Encrypt(buffer);
            buffer = BitConverter.GetBytes((short)buffer.Length).Concat(buffer).ToArray();

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

        private void Encrypt(byte[] buffer)
        {
            Crypt(buffer);
        }

        private void Decrypt(byte[] buffer)
        {
            Crypt(buffer);
        }

        private void Crypt(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; ++i)
            {
                buffer[i] ^= Key;
            }
        }

        private void ProcessInput()
        {
            lock (InputStream)
            {
                while (true)
                {
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

                            if (SocksServer.Instance.Clients.ContainsKey(id))
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
                        break;
                    }
                }
            }
        }
    }
}
