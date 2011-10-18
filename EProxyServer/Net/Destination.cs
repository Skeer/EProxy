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

        /// <summary>
        /// Create a new HTTP destination.
        /// </summary>
        /// <param name="parent">The connection that it belongs to.</param>
        /// <param name="id">The ID of the connection.</param>
        /// <param name="host">The hostname of the destination.</param>
        /// <param name="port">The port of the destination</param>
        public Destination(TunnelClient parent, short id, string host, short port)
        {
            Parent = parent;
            ID = id;
            Host = host;
            Port = port;

            // Getting IP address
            IPAddress address;
            // In the form of "xxx.xxx.xxx.xxx"
            if (!IPAddress.TryParse(Host, out address))
            {
                // In the form of "example.com"
                address = Dns.GetHostAddresses(Host).First(x => x.AddressFamily == AddressFamily.InterNetwork);
            }

            // Linking IP address and port
            IPEndPoint endPoint = new IPEndPoint(address, Port);
            try
            {
                // Connecting to destination
                Client.Connect(endPoint);

                Console.WriteLine("Connected to destination at {0}.", endPoint);

                // Preparing for send and receive
                ReceiveArgs = TunnelServer.Instance.PopArgs();
                SendArgs = TunnelServer.Instance.PopArgs();
                ReceiveArgs.SetBuffer(new byte[1500], 0, 1500);
                ReceiveArgs.Completed += Receive_Completed;
                SendArgs.Completed += Send_Completed;
                InputStream = new MemoryStream();
                OutputStream = new MemoryStream();

                // Begin receiving
                if (!Client.ReceiveAsync(ReceiveArgs))
                {
                    Receive_Completed(Client, ReceiveArgs);
                }
            }
            catch
            {
                // Failed to connect, disposing
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
            try
            {
                //Console.WriteLine("Sent {0} bytes.", e.BytesTransferred);

                // Telling the program that ProcessOutput() has completed
                // 1 = completed
                // 0 = in progress
                Interlocked.Exchange(ref OutstandingSends, 1);
                ProcessOutput();
            }
            catch
            {
                Disconnect();
            }
        }

        public void Disconnect(bool invoked = false)
        {
            lock (this)
            {
                if (!Disposed)
                {
                    try
                    {
                        if (invoked)
                            Console.WriteLine("Disconnected from destination at {0}.", Client.RemoteEndPoint);
                        else
                        Console.WriteLine("Destination disconnected from {0}.", Client.RemoteEndPoint);
                    }
                    catch
                    {
                        if (invoked)
                            Console.WriteLine("Disconnected from desnation.");
                        else
                            Console.WriteLine("Destination disconnected.");
                    }
                    // Disconnected
                    Dispose();
                }
            }
        }

        public void Send(byte[] buffer, int offset, int count)
        {
            try
            {
                // Making sure we don't modify OutputStream at the same time
                lock (OutputStream)
                {
                    long pos = OutputStream.Position;
                    OutputStream.Position = OutputStream.Length;
                    // Write data to OutputStream
                    OutputStream.Write(buffer, offset, count);
                    OutputStream.Position = pos;
                }

                ProcessOutput();
            }
            catch
            {
                Disconnect();
            }
        }

        private void ProcessOutput()
        {
            // Check if there is data, or ProcessOutput() is already running, stop if true
            if (OutputStream.Length == OutputStream.Position || Interlocked.Exchange(ref OutstandingSends, 0) != 1)
                return;

            lock (OutputStream)
            {
                byte[] buffer = new byte[OutputStream.Length - OutputStream.Position];
                // Read all data
                OutputStream.Read(buffer, 0, buffer.Length);
                OutputStream.SetLength(0);
                SendArgs.SetBuffer(buffer, 0, buffer.Length);
            }

            // Send data
            if (!Client.SendAsync(SendArgs))
            {
                Send_Completed(Client, SendArgs);
            }
        }

        private void Receive_Completed(object sender, SocketAsyncEventArgs e)
        {
            try
            {
                // If receive was successful and there actually was data (0 bytes = disconnect)
                if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
                {
                    //Console.WriteLine("Received {0} bytes.", e.BytesTransferred);
                    lock (InputStream)
                    {
                        long pos = InputStream.Position;
                        InputStream.Position = InputStream.Length;
                        // Writing data to InputStream, to be processed later
                        InputStream.Write(e.Buffer, 0, e.BytesTransferred);
                        InputStream.Position = pos;

                    }
                    ProcessInput();

                    // Begin receiving again
                    if (!Client.ReceiveAsync(ReceiveArgs))
                    {
                        Receive_Completed(Client, ReceiveArgs);
                    }
                }
                else
                {
                    throw new Exception();
                }
            }
            catch
            {
                Disconnect();
            }
        }

        private void ProcessInput()
        {
            lock (InputStream)
            {
                while (true)
                {
                    // If there is data to process
                    if (InputStream.Length > InputStream.Position)
                    {
                        byte[] buffer = new byte[InputStream.Length - InputStream.Position];
                        InputStream.Read(buffer, 0, buffer.Length);
                        InputStream.SetLength(0);

                        // Relay all data to the parent connection
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
