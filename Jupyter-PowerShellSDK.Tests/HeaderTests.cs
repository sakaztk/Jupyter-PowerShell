using Xunit;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Jupyter_PowerShellSDK;
using Jupyter_PowerShellSDK.Models;

namespace Jupyter_PowerShellSDK.Tests;

public class HeaderTests
{
    [Fact]
    public void HeaderUsesJupyterProtocolVersion()
    {
        var header = Header.Create(Guid.NewGuid().ToString(), "kernel_info_request");

        Assert.Equal("5.3", header.Version);
        Assert.Equal("powershell7", header.Username);
        Assert.Equal("kernel_info_request", header.MessageType);
    }
}

public class ConnectionTests
{
    [Fact]
    public void ConnectionRequiresValidSocketPorts()
    {
        var connection = new Connection
        {
            IP = "127.0.0.1",
            Transport = "tcp",
            ShellPort = 1,
            IOPubPort = 2,
            HBPort = 3
        };

        Assert.False(string.IsNullOrWhiteSpace(connection.IP));
        Assert.True(connection.ShellPort > 0);
        Assert.True(connection.IOPubPort > 0);
        Assert.True(connection.HBPort > 0);
    }
}

public class SignatureTests
{
    [Fact]
    public void EmptyKeyProducesEmptySignature()
    {
        var kernel = new Kernel(new Connection
        {
            Key = string.Empty,
            SignatureScheme = "hmac-sha256"
        });

        Assert.Equal(string.Empty, kernel.SignMessage("header", "parent", "metadata", "content"));
    }
}

public class RunspaceTests
{
    [Fact]
    public void RunspaceExecutesSimpleScript()
    {
        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        var output = ps.AddScript("1+1").Invoke();

        Assert.Single(output);
        Assert.Equal(2, (int)output[0].BaseObject);
    }
}

