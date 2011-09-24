using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Net;

namespace EProxyServer.Net
{
    class Destination : IDisposable
    {
        private bool Disposed = false;
        private int OutstandingSends = 1;

        private TunnelClient Parent;
        private short Port;
        private short ID;
        private string Host;

        private Socket Client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private SocketAsyncEventArgs ReceiveArgs;
        private SocketAsyncEventArgs SendArgs;
        private MemoryStream InputStream = new MemoryStream();
        private MemoryStream OutputStream = new MemoryStream();

        public Destination(TunnelClient parent, short id, string host, short port)
        {
            Parent = parent;
            ID = id;
            Host = host;
            Port = port;

            IPAddress address;
            if (!IPAddress.TryParse(Host, out address))
            {
                address = Dns.GetHostAddresses(Host).First(x => x.AddressFamily == AddressFamily.InterNetwork);
            }

            IPEndPoint endPoint = new IPEndPoint(address, Port);
            try
            {
                Client.Connect(endPoint);

                ReceiveArgs = TunnelServer.Instance.PopArgs();
                SendArgs = TunnelServer.Instance.PopArgs();
                ReceiveArgs.SetBuffer(new byte[1500], 0, 1500);

                ReceiveArgs.Completed += Receive_Completed;
                SendArgs.Completed += Send_Completed;

                InputStream = new MemoryStream();
                OutputStream = new MemoryStream();

                if (!Client.ReceiveAsync(ReceiveArgs))
                {
                    Receive_Completed(Client, ReceiveArgs);
                }
            }
            catch
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Send_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (Client == null)
                return;
            //Console.WriteLine("Sent {0} bytes.", e.BytesTransferred);
            Interlocked.Exchange(ref OutstandingSends, 1);
            ProcessOutput();
        }

        public void Send(byte[] buffer, int offset, int count)
        {
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
                    //Console.WriteLine("Client disconnected from {0}.", Client.RemoteEndPoint);
                }
                catch
                {
                    //Console.WriteLine("Client disconnected.");
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
                    if (InputStream.Length > InputStream.Position)
                    {
                        byte[] buffer = new byte[InputStream.Length - InputStream.Position];
                        InputStream.Read(buffer, 0, buffer.Length);
                        InputStream.SetLength(0);

                        Parent.Send(ID, buffer);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                if (disposing)
                {
                    // managed

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

                    if (Parent != null && Parent.Destinations.ContainsKey(ID))
                    {
                        Parent.Destinations.Remove(ID);
                    }
                }
                // unmanaged
                Disposed = true;
            }
        }
    }
}
