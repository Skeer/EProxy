using System;

namespace EProxyClient
{
    class Program
    {
        static void Main(string[] args)
        {
            Net.SocksServer.Instance.Run();
            // asdf
            while (Console.ReadLine() != "q") ;
        }
    }
}
