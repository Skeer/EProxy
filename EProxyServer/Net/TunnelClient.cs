using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace EProxyServer.Net
{
    class TunnelClient
    {
        private byte Key = 0x53;

        private bool Disposed = false;
        private Socket Client;
        private SocketAsyncEventArgs SendArgs;
        private SocketAsyncEventArgs ReceiveArgs;
        private MemoryStream InputStream = new MemoryStream();
        private MemoryStream OutputStream = new MemoryStream();
        private int OutstandingSends = 1;

        private Socket Destination;
        private string DestinationHost;
        private int DestinationPort;
        private SocketAsyncEventArgs DestinationSendArgs;
        private SocketAsyncEventArgs DestinationReceiveArgs;
        private MemoryStream DestinationInputStream;
        private MemoryStream DestinationOutputStream;
        private int DestinationOutstandingSends = 1;

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
                    Console.WriteLine("Client disconnected from {0}.", Client.RemoteEndPoint);
                }
                catch
                {
                    Console.WriteLine("Client disconnected.");
                }
                Dispose();
            }
        }

        private void DestinationSend_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (Destination == null)
                return;
            //Console.WriteLine("Sent {0} bytes.", e.BytesTransferred);
            Interlocked.Exchange(ref DestinationOutstandingSends, 1);
            DestinationProcessOutput();
        }

        private void DestinationReceive_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (Destination == null)
                return;
            if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
            {
                //Console.WriteLine("Received {0} bytes.", e.BytesTransferred);
                lock (DestinationInputStream)
                {
                    long pos = DestinationInputStream.Position;
                    DestinationInputStream.Position = DestinationInputStream.Length;
                    DestinationInputStream.Write(e.Buffer, 0, e.BytesTransferred);
                    DestinationInputStream.Position = pos;

                }
                DestinationProcessInput();

                if (!Destination.ReceiveAsync(DestinationReceiveArgs))
                {
                    DestinationReceive_Completed(Destination, DestinationReceiveArgs);
                }
            }
            else
            {
                try{
                    Console.WriteLine("Tunnel disconnected from {0}.", Destination.RemoteEndPoint);
                }
                catch
                {
                    Console.WriteLine("Tunnel disconnected.");
                }
                Dispose();
            }
        }

        private void DestinationProcessInput()
        {
            lock (DestinationInputStream)
            {
                if (DestinationInputStream.Length > DestinationInputStream.Position)
                {
                    byte[] buffer = new byte[DestinationInputStream.Length - DestinationInputStream.Position];
                    DestinationInputStream.Read(buffer, 0, buffer.Length);
                    DestinationInputStream.SetLength(0);

                    Encrypt(buffer);

                    Send(BitConverter.GetBytes((short)buffer.Length).Concat(buffer).ToArray());
                }
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
                            if (Destination == null)
                            {
                                // short port
                                // short length
                                // string host
                                DestinationPort = BitConverter.ToInt16(buffer, 0);
                                length = BitConverter.ToInt16(buffer, 2);
                                DestinationHost = UTF8Encoding.UTF8.GetString(buffer, 4, length);

                                Destination = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                                IPAddress address;
                                if (!IPAddress.TryParse(DestinationHost, out address))
                                {
                                    address = Dns.GetHostAddresses(DestinationHost).First(x => x.AddressFamily == AddressFamily.InterNetwork);
                                }

                                IPEndPoint endPoint = new IPEndPoint(address, DestinationPort);
                                Destination.Connect(endPoint);
                                Console.WriteLine("Connected to destination at {0}.", endPoint);

                                DestinationReceiveArgs = TunnelServer.Instance.PopArgs();
                                DestinationSendArgs = TunnelServer.Instance.PopArgs();
                                DestinationReceiveArgs.SetBuffer(new byte[1500], 0, 1500);

                                DestinationReceiveArgs.Completed += DestinationReceive_Completed;
                                DestinationSendArgs.Completed += DestinationSend_Completed;

                                DestinationInputStream = new MemoryStream();
                                DestinationOutputStream = new MemoryStream();

                                if (!Destination.ReceiveAsync(DestinationReceiveArgs))
                                {
                                    DestinationReceive_Completed(Destination, DestinationReceiveArgs);
                                }
                            }
                            else
                            {
                                // standard request packet
                                lock (DestinationOutputStream)
                                {
                                    pos = DestinationOutputStream.Position;
                                    DestinationOutputStream.Position = DestinationOutputStream.Length;
                                    DestinationOutputStream.Write(buffer, 0, buffer.Length);
                                    DestinationOutputStream.Position = pos;
                                }

                                DestinationProcessOutput();
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
                        // not enough for packet length
                        break;
                    }
                }
            }
        }

        private void DestinationProcessOutput()
        {
            if (DestinationOutputStream.Length == DestinationOutputStream.Position || Interlocked.Exchange(ref DestinationOutstandingSends, 0) != 1)
                return;

            lock (DestinationOutputStream)
            {
                byte[] buffer = new byte[DestinationOutputStream.Length - DestinationOutputStream.Position];
                DestinationOutputStream.Read(buffer, 0, buffer.Length);
                DestinationOutputStream.SetLength(0);
                DestinationSendArgs.SetBuffer(buffer, 0, buffer.Length);
            }

            if (!Destination.SendAsync(DestinationSendArgs))
            {
                DestinationSend_Completed(Destination, DestinationSendArgs);
            }
        }

        private void Send(byte[] buffer)
        {
            // buffer is already encrypted with short length head
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

                    if (Destination != null)
                    {
                        Destination.Dispose();
                        Destination = null;
                    }

                    if (DestinationSendArgs != null)
                    {
                        DestinationSendArgs.Completed -= DestinationSend_Completed;
                        TunnelServer.Instance.PushArgs(DestinationSendArgs);
                        DestinationSendArgs = null;
                    }

                    if (DestinationReceiveArgs != null)
                    {
                        DestinationReceiveArgs.Completed -= DestinationReceive_Completed;
                        TunnelServer.Instance.PushArgs(DestinationReceiveArgs);
                        DestinationReceiveArgs = null;
                    }

                    if (DestinationInputStream != null)
                    {
                        DestinationInputStream.Dispose();
                        DestinationInputStream = null;
                    }

                    if (DestinationOutputStream != null)
                    {
                        DestinationOutputStream.Dispose();
                        DestinationOutputStream = null;
                    }
                }

                Disposed = true;
            }
        }
    }
}
