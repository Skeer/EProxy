using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace EProxyServer.Net
{
    class TunnelServer
    {
        public static TunnelServer Instance = new TunnelServer();
        private Socket Server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private int Port = 8125;
        private SocketAsyncEventArgs AcceptArgs = new SocketAsyncEventArgs();
        private Stack<SocketAsyncEventArgs> ArgsStack = new Stack<SocketAsyncEventArgs>();

        private TunnelServer() { }

        public void Run()
        {
            // Setting up connections
            AllocateArgs();

            // Preparing for conenctions
            Server.Bind(new IPEndPoint(IPAddress.Any, Port));
            Console.WriteLine("Bound to {0}.", Server.LocalEndPoint);
            Server.Listen(10);
            Console.WriteLine("Listening for incoming connections.");
            AcceptArgs.Completed += Accept_Completed;

            // Start accepting connections
            if (!Server.AcceptAsync(AcceptArgs))
            {
                Accept_Completed(Server, AcceptArgs);
            }
        }

        private void AllocateArgs()
        {
            for (int i = 0; i < 1000; ++i)
            {
                ArgsStack.Push(new SocketAsyncEventArgs());
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

            // Start new client per connection
            new TunnelClient(client);
        }

        // Memory management
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
    }
}
