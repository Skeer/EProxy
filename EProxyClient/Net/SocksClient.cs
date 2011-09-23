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
        private short ID;
        private bool Initialized = false;

        private bool Disposed = false;
        private Socket Client;
        private SocketAsyncEventArgs SendArgs;
        private SocketAsyncEventArgs ReceiveArgs;
        private MemoryStream InputStream = new MemoryStream();
        private MemoryStream OutputStream = new MemoryStream();
        private int OutstandingSends = 1;
        
        public SocksClient(short id, Socket client)
        {
            ID = id;
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

        private void ProcessInput()
        {
            lock (InputStream)
            {
                while (true)
                {
                    if (Initialized)
                    {
                        if (InputStream.Length > InputStream.Position)
                        {
                            byte[] buffer = new byte[InputStream.Length - InputStream.Position];
                            InputStream.Read(buffer, 0, buffer.Length);

                            SocksServer.Instance.Tunnel.Send(ID, buffer);
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
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

                                

                                buffer = BitConverter.GetBytes(port).Concat(BitConverter.GetBytes((short)ip.Length)).Concat(UTF8Encoding.UTF8.GetBytes(ip)).ToArray();

                                SocksServer.Instance.Tunnel.Send(ID, buffer);

                                // Accepted packet
                                Send(new byte[] { 0x00, 0x5a, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

                                Initialized = true;
                            }
                        }
                        else
                        {
                            long pos = InputStream.Position;
                            break;
                        }
                    }
                }
            }
        }

        public void Send(byte[] buffer, int offset = 0, int count = 0)
        {
            if (count == 0)
                count = buffer.Length - offset;

            lock (OutputStream)
            {
                long pos = OutputStream.Position;
                OutputStream.Position = OutputStream.Length;
                OutputStream.Write(buffer, offset, count);
                OutputStream.Position = pos;
            }

            ProcessOutput();
        }

        private void ProcessOutput()
        {
            if (Client == null || OutputStream.Length == OutputStream.Position || Interlocked.Exchange(ref OutstandingSends, 0) != 1)
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

                    if (SocksServer.Instance.Clients.ContainsKey(ID))
                    {
                        SocksServer.Instance.Clients.Remove(ID);
                    }

                    //if (Tunnel != null)
                    //{
                    //    Tunnel.Dispose();
                    //    Tunnel = null;
                    //}

                    //if (TunnelSendArgs != null)
                    //{
                    //    TunnelSendArgs.Completed -= TunnelSend_Completed;
                    //    SocksServer.Instance.PushArgs(TunnelSendArgs);
                    //    TunnelSendArgs = null;
                    //}

                    //if (TunnelReceiveArgs != null)
                    //{
                    //    TunnelReceiveArgs.Completed -= TunnelReceive_Completed;
                    //    SocksServer.Instance.PushArgs(TunnelReceiveArgs);
                    //    TunnelReceiveArgs = null;
                    //}

                    //if (TunnelInputStream != null)
                    //{
                    //    TunnelInputStream.Dispose();
                    //    TunnelInputStream = null;
                    //}

                    //if (TunnelOutputStream != null)
                    //{
                    //    TunnelOutputStream.Dispose();
                    //    TunnelOutputStream = null;
                    //}
                }

                Disposed = true;
            }
        }
    }
}
