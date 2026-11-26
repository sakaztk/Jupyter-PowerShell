using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jupyter_PowerShell7.Models;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json.Linq;
using StreamModel = Jupyter_PowerShell7.Models.Stream;

namespace Jupyter_PowerShell7
{


public sealed class Kernel : IDisposable
{
    private readonly Connection connection;
    private readonly HMACSHA256 signatureProvider;
    private readonly PwshProcessSession pwsh;
    private CancellationTokenSource cancellationTokenSource;
    private Task heartbeatTask;
    private Task serverTask;

    private ResponseSocket heartbeatSocket;
    private RouterSocket shellSocket;
    private PublisherSocket ioPubSocket;
    private int executionCount;
    private string pwshVersion = "7";

    public RouterSocket ShellSocket => this.shellSocket!;
    public PublisherSocket IOPubSocket => this.ioPubSocket!;

    public Kernel(Connection connection, string executablePath = null)
    {
        this.connection = connection;
        if (!string.IsNullOrEmpty(connection.Key))
        {
            this.signatureProvider = new HMACSHA256(Encoding.ASCII.GetBytes(connection.Key));
        }

        this.pwsh = new PwshProcessSession(executablePath);
    }

    public void Start()
    {
        var versionResult = this.pwsh.ExecuteAsync("$PSVersionTable.PSVersion.ToString()").GetAwaiter().GetResult();
        this.pwshVersion = versionResult.StandardOutput.Trim().Split('\r', '\n').FirstOrDefault()?.Trim() ?? "7";

        this.cancellationTokenSource = new CancellationTokenSource();
        this.heartbeatTask = Task.Run(this.HeartbeatLoop);
        this.serverTask = Task.Run(this.ServerLoop);
    }

    public void Wait()
    {
        if (this.heartbeatTask is not null && this.serverTask is not null)
        {
            Task.WaitAll(this.heartbeatTask, this.serverTask);
        }
    }

    public void Stop()
    {
        if (this.cancellationTokenSource is null || this.cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        this.cancellationTokenSource.Cancel();

        this.pwsh.Stop();

        if (this.heartbeatSocket is not null)
        {
            this.heartbeatSocket.Dispose();
        }

        if (this.shellSocket is not null)
        {
            this.shellSocket.Dispose();
        }

        if (this.ioPubSocket is not null)
        {
            this.ioPubSocket.Dispose();
        }
    }

    public string SignMessage(params string[] parts)
    {
        if (this.signatureProvider is null)
        {
            return string.Empty;
        }

        this.signatureProvider.Initialize();
        foreach (var item in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(item);
            this.signatureProvider.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        this.signatureProvider.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(this.signatureProvider.Hash!).Replace("-", "").ToLowerInvariant();
    }

    public int Execute(string script, Message originalMessage, bool silent = false, CancellationToken cancellationToken = default)
    {
        this.executionCount++;

        if (!silent)
        {
        this.IOPubSocket.SendMessage(Message.Create(originalMessage, "execute_input", new
        {
            code = script,
            execution_count = this.executionCount
        }), this);
        }

        var result = this.pwsh.ExecuteAsync(script, cancellationToken).GetAwaiter().GetResult();
        if (silent)
        {
            return this.executionCount;
        }

        if (!string.IsNullOrEmpty(result.StandardOutput))
        {
            this.IOPubSocket.SendMessage(Message.Create(originalMessage, "stream", new StreamModel
            {
                Name = StreamModel.StdOut,
                Text = result.StandardOutput
            }), this);
        }

        if (!string.IsNullOrEmpty(result.StandardError))
        {
            this.IOPubSocket.SendMessage(Message.Create(originalMessage, "stream", new StreamModel
            {
                Name = StreamModel.StdErr,
                Text = result.StandardError
            }), this);
        }

        return this.executionCount;
    }

    public string IsComplete(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "complete";
        }

        var encodedCode = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
        var script = $@"
$__code=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedCode}'))
$tokens=$null
$errors=$null
[System.Management.Automation.Language.Parser]::ParseInput($__code,[ref]$tokens,[ref]$errors) | Out-Null
if ($errors | Where-Object {{$_.IncompleteInput}}) {{ 'incomplete' }}
elseif ($errors) {{ 'invalid' }}
else {{ 'complete' }}
";

        var result = this.pwsh.ExecuteAsync(script).GetAwaiter().GetResult();
        return result.StandardOutput.Trim().Split('\r', '\n')
            .Select(line => line.Trim())
            .LastOrDefault(line => line == "complete" || line == "incomplete" || line == "invalid") ?? "complete";
    }

    public (List<string> Matches, int CursorStart, int CursorEnd) Complete(string code, int cursorPosition)
    {
        var encodedCode = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
        var script = $@"
$__code=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedCode}'))
$r=TabExpansion2 -inputScript $__code -cursorColumn {cursorPosition}
[PSCustomObject]@{{
    Matches=@($r.CompletionMatches | ForEach-Object {{$_.CompletionText}})
    CursorStart=$r.ReplacementIndex
    CursorEnd=$r.ReplacementIndex+$r.ReplacementLength
}} | ConvertTo-Json -Compress -Depth 5
";

        var result = this.pwsh.ExecuteAsync(script).GetAwaiter().GetResult();
        var json = result.StandardOutput.Trim().Split('\r', '\n').LastOrDefault(line => line.TrimStart().StartsWith('{'));
        if (string.IsNullOrEmpty(json))
        {
            return (new List<string>(), cursorPosition, cursorPosition);
        }

        try
        {
            var parsed = JObject.Parse(json);
            var matches = (parsed["Matches"]?.ToObject<List<string>>() ?? new List<string>())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return (
                matches,
                parsed["CursorStart"]?.Value<int>() ?? cursorPosition,
                parsed["CursorEnd"]?.Value<int>() ?? cursorPosition);
        }
        catch
        {
            return (new List<string>(), cursorPosition, cursorPosition);
        }
    }

    public void Dispose()
    {
        this.Stop();
        this.pwsh.Dispose();
        this.signatureProvider?.Dispose();
        this.cancellationTokenSource?.Dispose();
    }

    private string Address(int port) => $"{this.connection.Transport}://{this.connection.IP}:{port}";

    private void HeartbeatLoop()
    {
        this.heartbeatSocket = new ResponseSocket(this.Address(this.connection.HBPort));
        while (this.cancellationTokenSource?.IsCancellationRequested == false)
        {
            var msg = new NetMQ.Msg();
            msg.InitEmpty();
            try
            {
                if (this.heartbeatSocket.TryReceive(ref msg, TimeSpan.FromMilliseconds(100)))
                {
                    this.heartbeatSocket.SendFrame(msg.Data);
                }
            }
            finally
            {
                msg.Close();
            }
        }
    }

    private void ServerLoop()
    {
        this.shellSocket = new RouterSocket(this.Address(this.connection.ShellPort));
        this.ioPubSocket = new PublisherSocket(this.Address(this.connection.IOPubPort));

        var sessionId = Guid.NewGuid().ToString();
        this.IOPubSocket.SendMessage(Message.Create(sessionId, "status", new Status
        {
            ExecutionState = Status.Starting
        }), this);

        while (this.cancellationTokenSource?.IsCancellationRequested == false)
        {
            var message = this.ShellSocket.ReceiveMessage(this);
        if (!string.IsNullOrEmpty(message.Signature))
        {
            this.IOPubSocket.SendMessage(Message.Create(message, "status", new Status
            {
                ExecutionState = Status.Busy
            }), this);

            switch (message.Header.MessageType)
            {
                case "kernel_info_request":
                    HandleKernelInfo(message);
                    break;
                case "execute_request":
                    Task.Run(() => HandleExecute(message));
                    break;
                case "complete_request":
                    HandleComplete(message);
                    break;
                case "is_complete_request":
                    HandleIsComplete(message);
                    break;
                case "interrupt_request":
                    this.pwsh.Interrupt();
                    this.ShellSocket.SendMessage(Message.Create(message, "interrupt_reply", new
                    {
                        status = ReplyStatusBase.Ok
                    }), this);
                    break;
                case "shutdown_request":
                    this.ShellSocket.SendMessage(Message.Create(message, "shutdown_reply", new
                    {
                        status = ReplyStatusBase.Ok,
                        restart = false
                    }), this);
                    this.cancellationTokenSource?.Cancel();
                    break;
            }

            this.IOPubSocket.SendMessage(Message.Create(message, "status", new Status
            {
                ExecutionState = Status.Idle
            }), this);

            if (this.cancellationTokenSource?.IsCancellationRequested == true)
            {
                break;
            }
        }
        }
    }

    private void HandleKernelInfo(Message message)
    {
        this.ShellSocket.SendMessage(Message.Create(message, "kernel_info_reply", new KernelInfoReply
        {
            Status = ReplyStatusBase.Ok,
            ProtocolVersion = "5.3",
            Implementation = "powershell7",
            ImplementationVersion = typeof(Kernel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            LanguageInfo = new LanguageInfo
            {
                Name = "powershell",
                Version = this.pwshVersion,
                MimeType = "application/x-powershell",
                FileExtension = "ps1",
                PygmentsLexer = "powershell",
                CodemirrorMode = "powershell",
                NbconvertExporter = "script"
            },
            Banner = $"PowerShell {this.pwshVersion} ({Environment.OSVersion})"
        }), this);
    }

    private void HandleExecute(Message message)
    {
        var request = message.Content.ToObject<ExecuteRequest>() ?? new ExecuteRequest();

        try
        {
            var count = this.Execute(request.Code ?? string.Empty, message, request.Silent, this.cancellationTokenSource?.Token ?? default);
            this.ShellSocket.SendMessage(Message.Create(message, "execute_reply", new ExecuteReplyOk
            {
                Status = ReplyStatusBase.Ok,
                ExecutionCount = count,
                UserExpressions = request.UserExpressions ?? new Dictionary<string, string>()
            }), this);
        }
        catch (Exception ex)
        {
            this.IOPubSocket.SendMessage(Message.Create(message, "execute_error", new
            {
                ename = ex.GetType().Name,
                evalue = ex.Message,
                traceback = Array.Empty<string>()
            }), this);

            this.ShellSocket.SendMessage(Message.Create(message, "execute_reply", new
            {
                status = ReplyStatusBase.Error,
                execution_count = this.executionCount,
                ename = ex.GetType().Name,
                evalue = ex.Message,
                traceback = Array.Empty<string>()
            }), this);
        }
    }

    private void HandleIsComplete(Message message)
    {
        var code = message.Content.Value<string>("code") ?? string.Empty;
        var status = this.IsComplete(code);

        this.ShellSocket.SendMessage(Message.Create(message, "is_complete_reply", new IsCompleteReply
        {
            Status = status,
            Indent = status == "incomplete" ? "    " : string.Empty
        }), this);
    }

    private void HandleComplete(Message message)
    {
        var request = message.Content.ToObject<CompleteRequest>() ?? new CompleteRequest();
        var result = this.Complete(request.Code ?? string.Empty, request.CursorPosition);

        this.ShellSocket.SendMessage(Message.Create(message, "complete_reply", new CompleteReply
        {
            Status = ReplyStatusBase.Ok,
            Matches = result.Matches,
            CursorStart = result.CursorStart,
            CursorEnd = result.CursorEnd
        }), this);
    }
}
}
