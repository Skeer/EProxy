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
        private byte Key = 0x53;

        private bool Disposed = false;
        private Socket Client;
        private SocketAsyncEventArgs SendArgs;
        private SocketAsyncEventArgs ReceiveArgs;
        private MemoryStream InputStream = new MemoryStream();
        private MemoryStream OutputStream = new MemoryStream();
        private int OutstandingSends = 1;

        public Dictionary<short, Destination> Destinations = new Dictionary<short,Destination>();
        
        /// 
        /// multiple destinations per tunnelclient
        /// destinations identified by short id
        /// receive packet is:
        ///   short len, byte[] encrypted
        ///     encrypted is either:
        ///       short id, short port, short len, string host
        ///       short id, byte[] data
        /// send packet is:
        ///   short len, byte[] encrypted
        ///     encrypted is:
        ///       short id, byte[] data
        /// destinationreceive:
        ///   

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
                                if (length == buffer.Length - 6)
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
