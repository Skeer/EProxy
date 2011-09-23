using System;

namespace EProxyClient
{
    class Program
    {
        static void Main(string[] args)
        {
            Net.SocksServer.Instance.Run();
            
            while (Console.ReadLine() != "q") ;
        }
    }
}
