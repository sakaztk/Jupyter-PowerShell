using System;
using System.IO;
using Jupyter_PowerShell5.Models;
using Newtonsoft.Json;

namespace Jupyter_PowerShell5
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Please launch this executable as Jupyter kernel.");
                return;
            }

            try
            {
                Console.WriteLine($"Connection file: {args[0]}");
                var connection = JsonConvert.DeserializeObject<Connection>(File.ReadAllText(args[0]));
                if (connection == null || string.IsNullOrWhiteSpace(connection.IP) || connection.ShellPort <= 0 || connection.IOPubPort <= 0 || connection.HBPort <= 0)
                {
                    Console.WriteLine($"Could not read connection file: {args[0]}");
                    return;
                }

                var kernel = new Kernel(connection);
                kernel.Start();
                kernel.Wait();
                kernel.Stop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not start kernel: {ex.Message}");
            }
        }
    }
}