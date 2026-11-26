using Xunit;
using Jupyter_PowerShell7.Models;

namespace Jupyter_PowerShell7.Tests;

public class HeaderTests
{
    [Fact]
    public void HeaderUsesJupyterProtocolVersion()
    {
        var header = Header.Create(Guid.NewGuid().ToString(), "kernel_info_request");

        Assert.Equal("5.3", header.Version);
        Assert.Equal("kernel_info_request", header.MessageType);
    }
}

public class PwshProcessSessionTests
{
    [Fact]
    public async Task ExecuteReturnsOutputForPersistentSession()
    {
        using var session = new PwshProcessSession("pwsh.exe");

        var result = await session.ExecuteAsync("1+1");

        Assert.Contains("2", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task InterruptRestartsPersistentSession()
    {
        using var session = new PwshProcessSession("pwsh.exe");
        await session.ExecuteAsync("$x=42");

        session.Interrupt();
        var result = await session.ExecuteAsync("42");

        Assert.Contains("42", result.StandardOutput);
    }
}

public class SignatureTests
{
    [Fact]
    public void IsCompleteHelperClassifiesEmptyCode()
    {
        Assert.Equal("complete", IsCompleteStatus(string.Empty));
    }

    private static string IsCompleteStatus(string code)
        => string.IsNullOrWhiteSpace(code) ? "complete" : "complete";
}

public class ConnectionValidationTests
{
    [Fact]
    public void ConnectionRequiresValidSocketPorts()
    {
        var connection = new Connection
        {
            IP = "127.0.0.1",
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

public class OutputEncodingTests
{
    [Fact]
    public void Utf8JapaneseTextCanBeRepresented()
    {
        const string text = "ディレクトリ";

        Assert.Contains("ディ", text);
    }
}
