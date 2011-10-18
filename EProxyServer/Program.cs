using System;

namespace EProxyServer
{
    class Program
    {
        /// <summary>
        /// Program entry point.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        static void Main(string[] args)
        {
            // Run the server
            Net.TunnelServer.Instance.Run();

            // Continue running unless "q" is typed.
            while (Console.ReadLine() != "q") ;
        }
    }
}
