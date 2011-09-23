using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace EProxyClient.Net
{
    class SocksClient : IDisposable
    {
        private byte Key = 0x53;

        private bool Disposed = false;
        private Socket Client;
        private SocketAsyncEventArgs SendArgs;
        private SocketAsyncEventArgs ReceiveArgs;
        private MemoryStream InputStream = new MemoryStream();
        private MemoryStream OutputStream = new MemoryStream();
        private int OutstandingSends = 1;

        private Socket Tunnel;
        private string TunnelHost = "skeerhouse.net";
        private int TunnelPort = 8125;
        private SocketAsyncEventArgs TunnelSendArgs;
        private SocketAsyncEventArgs TunnelReceiveArgs;
        private MemoryStream TunnelInputStream;
        private MemoryStream TunnelOutputStream;
        private int TunnelOutstandingSends = 1;

        public SocksClient(Socket client)
        {
            Client = client;

            SendArgs = SocksServer.Instance.PopArgs();
            ReceiveArgs = SocksServer.Instance.PopArgs();

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
            //Console.WriteLine("Sent {0} bytes.", e.BytesTransferred);
            Interlocked.Exchange(ref OutstandingSends, 1);
            ProcessOutput();
        }

        private void Receive_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (Client == null)
                return;
            if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
            {
                //Console.WriteLine("Received {0} bytes.", e.BytesTransferred);
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
                    Console.WriteLine("Client disconnected from {0}.", Client.RemoteEndPoint);
                }
                catch
                {
                    Console.WriteLine("Client disconnected.");
                }
                Dispose();
            }
        }

        private void TunnelSend_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (Tunnel == null)
                return;
            //Console.WriteLine("Tunnel sent {0} bytes.", e.BytesTransferred);
            Interlocked.Exchange(ref TunnelOutstandingSends, 1);
            TunnelProcessOutput();
        }

        private void TunnelReceive_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (Tunnel == null)
                return;
            if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
            {
                //Console.WriteLine("Tunnel received {0} bytes.", e.BytesTransferred);
                lock (TunnelInputStream)
                {
                    long pos = TunnelInputStream.Position;
                    TunnelInputStream.Position = TunnelInputStream.Length;
                    TunnelInputStream.Write(e.Buffer, 0, e.BytesTransferred);
                    TunnelInputStream.Position = pos;

                }
                TunnelProcessInput();

                if (!Tunnel.ReceiveAsync(TunnelReceiveArgs))
                {
                    TunnelReceive_Completed(Tunnel, TunnelReceiveArgs);
                }
            }
            else
            {
                try
                {
                    Console.WriteLine("Tunnel disconnected from {0}.", Tunnel.RemoteEndPoint);
                }
                catch
                {
                    Console.WriteLine("Tunnel disconnected.");
                }
                Dispose();
            }
        }

        private void TunnelProcessInput()
        {
            lock (TunnelInputStream)
            {
                while (true)
                {
                    if (TunnelInputStream.Length - TunnelInputStream.Position >= 2)
                    {
                        long pos = TunnelInputStream.Position;
                        byte[] buffer = new byte[2];
                        TunnelInputStream.Read(buffer, 0, 2);
                        short length = BitConverter.ToInt16(buffer, 0);

                        if (TunnelInputStream.Length - TunnelInputStream.Position >= length)
                        {
                            buffer = new byte[length];
                            TunnelInputStream.Read(buffer, 0, buffer.Length);

                            Decrypt(buffer);

                            Send(buffer);
                        }
                        else
                        {
                            TunnelInputStream.Position = pos;
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
                    if (Tunnel == null)
                    {
                        if (InputStream.Length - InputStream.Position >= 9)
                        {
                            long pos = InputStream.Position;
                            byte[] buffer = new byte[8];
                            InputStream.Read(buffer, 0, buffer.Length);
                            if (buffer[0] == 0x04 && buffer[1] == 0x01)
                            {
                                short port = IPAddress.HostToNetworkOrder(BitConverter.ToInt16(buffer, 2));
                                string ip = String.Format("{0}.{1}.{2}.{3}", buffer[4], buffer[5], buffer[6], buffer[7]);
                                while (true)
                                {
                                    if (InputStream.Length == InputStream.Position)
                                    {
                                        InputStream.Position = pos;
                                        return;
                                    }
                                    else
                                    {
                                        if (InputStream.ReadByte() == 0)
                                        {
                                            break;
                                        }
                                    }
                                }

                                Tunnel = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                                IPAddress address;
                                if (!IPAddress.TryParse(TunnelHost, out address))
                                {
                                    address = Dns.GetHostAddresses(TunnelHost).First(x => x.AddressFamily == AddressFamily.InterNetwork);
                                }

                                IPEndPoint endPoint = new IPEndPoint(address, TunnelPort);
                                Tunnel.Connect(endPoint);
                                Console.WriteLine("Connected to tunnel at {0}.", endPoint);

                                TunnelReceiveArgs = SocksServer.Instance.PopArgs();
                                TunnelSendArgs = SocksServer.Instance.PopArgs();
                                TunnelReceiveArgs.SetBuffer(new byte[1500], 0, 1500);

                                TunnelReceiveArgs.Completed += TunnelReceive_Completed;
                                TunnelSendArgs.Completed += TunnelSend_Completed;

                                TunnelInputStream = new MemoryStream();
                                TunnelOutputStream = new MemoryStream();

                                if (!Tunnel.ReceiveAsync(TunnelReceiveArgs))
                                {
                                    TunnelReceive_Completed(Tunnel, TunnelReceiveArgs);
                                }

                                buffer = BitConverter.GetBytes(port).Concat(BitConverter.GetBytes((short)ip.Length)).Concat(UTF8Encoding.UTF8.GetBytes(ip)).ToArray();

                                Encrypt(buffer);
                                TunnelSend(BitConverter.GetBytes((short)buffer.Length).Concat(buffer).ToArray());

                                // Accepted packet
                                Send(new byte[] { 0x00, 0x5a, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
                            }
                        }
                        else
                        {
                            long pos = InputStream.Position;


                            break;
                        }
                    }
                    else
                    {
                        if (InputStream.Length > InputStream.Position)
                        {
                            byte[] buffer = new byte[InputStream.Length - InputStream.Position];
                            InputStream.Read(buffer, 0, buffer.Length);


                            Encrypt(buffer);
                            TunnelSend(BitConverter.GetBytes((short)buffer.Length).Concat(buffer).ToArray());
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }

        private void TunnelSend(byte[] buffer)
        {
            lock (TunnelOutputStream)
            {
                long pos = TunnelOutputStream.Position;
                TunnelOutputStream.Position = TunnelOutputStream.Length;
                TunnelOutputStream.Write(buffer, 0, buffer.Length);
                TunnelOutputStream.Position = pos;
            }

            TunnelProcessOutput();
        }

        private void TunnelProcessOutput()
        {
            // 0 = used, 1 = free
            if (TunnelOutputStream.Length == TunnelOutputStream.Position || Interlocked.Exchange(ref TunnelOutstandingSends, 0) != 1)
                return;

            lock (TunnelOutputStream)
            {
                byte[] buffer = new byte[TunnelOutputStream.Length - TunnelOutputStream.Position];
                TunnelOutputStream.Read(buffer, 0, buffer.Length);
                TunnelOutputStream.SetLength(0);
                TunnelSendArgs.SetBuffer(buffer, 0, buffer.Length);
            }

            if (!Tunnel.SendAsync(TunnelSendArgs))
            {
                TunnelSend_Completed(Tunnel, TunnelSendArgs);
            }
        }

        private void Send(byte[] buffer)
        {
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
                        SocksServer.Instance.PushArgs(SendArgs);
                        SendArgs = null;
                    }

                    if (ReceiveArgs != null)
                    {
                        ReceiveArgs.Completed -= Receive_Completed;
                        SocksServer.Instance.PushArgs(ReceiveArgs);
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

                    if (Tunnel != null)
                    {
                        Tunnel.Dispose();
                        Tunnel = null;
                    }

                    if (TunnelSendArgs != null)
                    {
                        TunnelSendArgs.Completed -= TunnelSend_Completed;
                        SocksServer.Instance.PushArgs(TunnelSendArgs);
                        TunnelSendArgs = null;
                    }

                    if (TunnelReceiveArgs != null)
                    {
                        TunnelReceiveArgs.Completed -= TunnelReceive_Completed;
                        SocksServer.Instance.PushArgs(TunnelReceiveArgs);
                        TunnelReceiveArgs = null;
                    }

                    if (TunnelInputStream != null)
                    {
                        TunnelInputStream.Dispose();
                        TunnelInputStream = null;
                    }

                    if (TunnelOutputStream != null)
                    {
                        TunnelOutputStream.Dispose();
                        TunnelOutputStream = null;
                    }
                }

                Disposed = true;
            }
        }
    }
}
