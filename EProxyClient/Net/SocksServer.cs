using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace EProxyClient.Net
{
    class SocksServer
    {
        public static SocksServer Instance = new SocksServer();
        private Socket Server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private int Port = 8080;
        private SocketAsyncEventArgs AcceptArgs = new SocketAsyncEventArgs();
        private Stack<SocketAsyncEventArgs> ArgsStack = new Stack<SocketAsyncEventArgs>();
        private short Count = 0;
        public Dictionary<short, SocksClient> Clients = new Dictionary<short, SocksClient>();
        public Tunnel Tunnel;

        private SocksServer() { }

        public void Run()
        {
            // Proxy settings
            Console.Write("Use HTTP proxy [y/n]? ");
            if (Console.ReadLine() == "y")
            {
                Console.Write("Proxy host: ");
                string host = Console.ReadLine();
                Console.Write("Proxy port: ");
                int port = int.Parse(Console.ReadLine());
                Tunnel = new Tunnel(host, port);
            }
            else
            {
                Tunnel = new Tunnel();
            }

            AllocateArgs();

            Server.Bind(new IPEndPoint(IPAddress.Any, Port));
            Console.WriteLine("Bound to {0}.", Server.LocalEndPoint);
            Server.Listen(10);
            Console.WriteLine("Listening for incoming connections.");
            AcceptArgs.Completed += Accept_Completed;
            if (!Server.AcceptAsync(AcceptArgs))
            {
                Accept_Completed(Server, AcceptArgs);
            }
        }

        private void Accept_Completed(object sender, SocketAsyncEventArgs e)
        {
            Socket client = e.AcceptSocket;
            Console.WriteLine("Accepted connection from {0}.", client.RemoteEndPoint);
            e.AcceptSocket = null;
            if (!Server.AcceptAsync(AcceptArgs))
            {
                Accept_Completed(Server, AcceptArgs);
            }

            Clients.Add(Count, new SocksClient(Count++, client));
        }

        private void AllocateArgs()
        {
            for (int i = 0; i < 1000; ++i)
            {
                ArgsStack.Push(new SocketAsyncEventArgs());
            }
        }

        public SocketAsyncEventArgs PopArgs()
        {
            if (ArgsStack.Count > 0)
            {
                return ArgsStack.Pop();
            }
            return new SocketAsyncEventArgs();
        }

        public void PushArgs(SocketAsyncEventArgs e)
        {
            ArgsStack.Push(e);
        }

        public void RemoveClient(short id)
        {
            lock (Clients)
            {
                if (Clients.ContainsKey(id))
                {
                    Clients.Remove(id);
                    this.Tunnel.SendDisconnect(id);
                }
            }

        }
    }
}
