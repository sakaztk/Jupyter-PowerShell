using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Management.Automation;
using Jupyter_PowerShell5.Models;

namespace Jupyter_PowerShell5
{
    public static class MessageHandlers
    {
        private static readonly string PowerShellVersion = typeof(PSObject).Assembly.GetName().Version?.ToString(3) ?? "5.1";

        public static void HandleKernelInfo(Kernel kernel, Message message)
        {
            kernel.ShellSocket.SendMessage(Message.Create(
                message, "kernel_info_reply", new KernelInfoReply
                {
                    Status = ReplyStatusBase.Ok,
                    ProtocolVersion = "5.3",
                    Implementation = "powershell5",
                    ImplementationVersion = typeof(Kernel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                    LanguageInfo = new LanguageInfo
                    {
                        Name = "powershell",
                        Version = PowerShellVersion,
                        MimeType = "application/x-powershell",
                        FileExtension = "ps1",
                        PygmentsLexer = "powershell",
                        CodemirrorMode = "powershell",
                        NbconvertExporter = "script"
                    },
                    Banner = $"Windows PowerShell {PowerShellVersion} ({Environment.OSVersion})"
                }), kernel);
        }

        public static void HandleExecute(Kernel kernel, Message message)
        {
            var req = message.Content.ToObject<ExecuteRequest>() ?? new ExecuteRequest();
            kernel.QueueExecution(() =>
            {
                var count = kernel.InvokePowerShell(req.Code ?? string.Empty, message);

                if (!req.Silent)
                {
                    if (!kernel.LastExecutionSucceeded)
                    {
                        kernel.IOPubSocket.SendMessage(Message.Create(message, "execute_error", new
                        {
                            ename = "PowerShellRuntimeException",
                            evalue = "PowerShell execution failed.",
                            traceback = Array.Empty<string>()
                        }), kernel);
                    }

                    kernel.IOPubSocket.SendMessage(Message.Create(message, "execute_result", new
                    {
                        execution_count = count,
                        data = new Dictionary<string, string>(),
                        metadata = new Dictionary<string, object>()
                    }), kernel);
                }

                kernel.ShellSocket.SendMessage(Message.Create(
                    message,
                    "execute_reply",
                    kernel.LastExecutionSucceeded
                        ? new ExecuteReplyOk
                        {
                            Status = ReplyStatusBase.Ok,
                            ExecutionCount = count,
                            UserExpressions = new Dictionary<string, string>()
                        }
                        : new
                        {
                            status = ReplyStatusBase.Error,
                            execution_count = count,
                            ename = "PowerShellRuntimeException",
                            evalue = "PowerShell execution failed.",
                            traceback = Array.Empty<string>()
                        }), kernel);
            });
        }

        public static void HandleInterrupt(Kernel kernel, Message message)
        {
            kernel.Interrupt();
            kernel.ShellSocket.SendMessage(Message.Create(message, "interrupt_reply", new
            {
                status = ReplyStatusBase.Ok
            }), kernel);
        }

        public static void HandleIsComplete(Kernel kernel, Message message)
        {
            var code = message.Content.Value<string>("code") ?? string.Empty;
            var status = code.TrimEnd().EndsWith("{") || code.TrimEnd().EndsWith("[") || code.TrimEnd().EndsWith("(")
                ? "incomplete"
                : "complete";

            kernel.ShellSocket.SendMessage(Message.Create(message, "is_complete_reply", new
            {
                status,
                indent = status == "incomplete" ? "    " : string.Empty
            }), kernel);
        }

        public static void HandleShutdown(Kernel kernel, Message message)
        {
            kernel.ShellSocket.SendMessage(Message.Create(message, "shutdown_reply", new
            {
                status = ReplyStatusBase.Ok,
                restart = message.Content.Value<bool>("restart")
            }), kernel);
            kernel.RequestShutdown();
        }

        public static void HandleComplete(Kernel kernel, Message message)
        {
            var req = message.Content.ToObject<CompleteRequest>();
            var result = kernel.InvokeCommandCompletion(req);
            kernel.ShellSocket.SendMessage(Message.Create(
                message, "complete_reply", new CompleteReply
                {
                    Matches = result.CompletionMatches.Select(match => match.CompletionText).ToList(),
                    CursorStart = result.ReplacementIndex,
                    CursorEnd = result.ReplacementIndex + result.ReplacementLength
                }), kernel);
        }
    }
}