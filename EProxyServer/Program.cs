using System;

namespace EProxyServer
{
    class Program
    {
        static void Main(string[] args)
        {
            Net.TunnelServer.Instance.Run();

            while (Console.ReadLine() != "q") ;
        }
    }
}
