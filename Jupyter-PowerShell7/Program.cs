using Jupyter_PowerShell7;
using Jupyter_PowerShell7.Models;
using Newtonsoft.Json;

if (args.Length == 0)
{
    Console.WriteLine("Please launch this executable as a Jupyter kernel with a connection file.");
    return 1;
}

var connectionFile = args[0];
var executablePath = args.Length > 1 ? args[1] : null;
if (args.Length > 1 && !Path.IsPathFullyQualified(executablePath) && !string.IsNullOrWhiteSpace(executablePath))
{
    executablePath = Path.GetFullPath(executablePath);
}

var connection = JsonConvert.DeserializeObject<Connection>(File.ReadAllText(connectionFile));

if (connection is null || string.IsNullOrWhiteSpace(connection.IP) || connection.ShellPort <= 0 || connection.IOPubPort <= 0 || connection.HBPort <= 0)
{
    Console.WriteLine($"Could not read connection file: {connectionFile}");
    return 1;
}

using var kernel = new Kernel(connection, executablePath);
Console.WriteLine($"Connection file: {connectionFile}");
Console.WriteLine("Starting PowerShell 7 kernel");
kernel.Start();
kernel.Wait();
Console.WriteLine("PowerShell 7 kernel stopped");
return 0;
