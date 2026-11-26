using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jupyter_PowerShell5.Models;
using NetMQ;
using NetMQ.Sockets;

namespace Jupyter_PowerShell5
{
    public class Kernel
    {
        public delegate void HandleMessage(Kernel kernel, Message message);

        public ResponseSocket HBSocket { get; set; }
        public RouterSocket ShellSocket { get; set; }
        public PublisherSocket IOPubSocket { get; set; }

        private readonly Connection connection;
        private readonly HMAC signatureProvider;
        private Task heartbeatTask;
        private Task serverTask;
        private CancellationTokenSource cancellationTokenSource;
        private NetMQPoller serverPoller;
        private Runspace psRunspace;
        private PowerShell activePowerShell;
        private Task executionTask;
        private readonly object executionLock = new object();
        private readonly object executionTaskLock = new object();
        private int executionCount;
        private bool lastExecutionSucceeded = true;

        public bool LastExecutionSucceeded => this.lastExecutionSucceeded;

        public Kernel(Connection connection)
        {
            this.connection = connection;
            if (!string.IsNullOrEmpty(this.connection.Key))
            {
                this.signatureProvider =
                    HMAC.Create(this.connection.SignatureScheme.Replace("-", string.Empty).ToUpperInvariant());
                this.signatureProvider.Key = Encoding.ASCII.GetBytes(this.connection.Key);
            }
        }

        public void Start()
        {
            lock (this)
            {
                this.Stop();
                Console.WriteLine($"Creating PowerShell session");
                this.psRunspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
                this.psRunspace.Open();

                Console.WriteLine("Starting kernel");
                this.cancellationTokenSource = new CancellationTokenSource();
                this.heartbeatTask = Task.Run(this.HeartbeatTask);
                this.serverTask = Task.Run(this.ServerTask);
            }
        }

        public void Wait()
        {
            Task executionTask;
            lock (this.executionTaskLock)
            {
                executionTask = this.executionTask;
            }

            var tasks = new List<Task>();
            if (this.heartbeatTask != null) tasks.Add(this.heartbeatTask);
            if (this.serverTask != null) tasks.Add(this.serverTask);
            if (executionTask != null) tasks.Add(executionTask);
            if (tasks.Count > 0) Task.WaitAll(tasks.ToArray());
        }

        public void QueueExecution(Action action)
        {
            lock (this.executionTaskLock)
            {
                this.executionTask = Task.Run(action);
            }
        }

        public void Stop()
        {
            lock (this)
            {
                if (this.cancellationTokenSource != null && !this.cancellationTokenSource.IsCancellationRequested)
                {
                    Console.WriteLine("Stopping kernel");
                    this.cancellationTokenSource.Cancel();
                    try { this.activePowerShell?.Stop(); } catch { }
                    this.serverPoller?.Stop();
                }
            }

            this.Wait();

            lock (this)
            {
                if (this.psRunspace != null)
                {
                    Console.WriteLine("Closing PowerShell session");
                    this.psRunspace.Close();
                    this.psRunspace.Dispose();
                    this.psRunspace = null;
                }
            }
        }

        public void RequestShutdown()
        {
            if (this.cancellationTokenSource != null)
            {
                this.cancellationTokenSource.Cancel();
                this.serverPoller?.Stop();
            }
        }

        public string SignMessage(params string[] parts)
        {
            if (this.signatureProvider == null)
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
            return BitConverter.ToString(this.signatureProvider.Hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        public int InvokePowerShell(string script, Message originalMessage)
        {
            lock (this.executionLock)
            {
            using var ps = PowerShell.Create();
            this.activePowerShell = ps;
            ps.Runspace = this.psRunspace;
            ps.Streams.Verbose.DataAdded += this.GetStreamHandler(index =>
                $"VERBOSE: {ps.Streams.Verbose[index]}", ps, Stream.StdOut, originalMessage);
            ps.Streams.Information.DataAdded += this.GetStreamHandler(index =>
                ps.Streams.Information[index].ToString(), ps, Stream.StdOut, originalMessage);
            ps.Streams.Warning.DataAdded += this.GetStreamHandler(index =>
                $"WARNING: {ps.Streams.Warning[index]}", ps, Stream.StdOut, originalMessage);
            ps.Streams.Error.DataAdded += this.GetStreamHandler(index =>
                ps.Streams.Error[index].ToString(), ps, Stream.StdErr, originalMessage);

            this.lastExecutionSucceeded = true;
            try
            {
                var outputs = ps.AddScript(script).AddCommand("Out-String").Invoke();
                this.IOPubSocket.SendMessage(Message.Create(
                    originalMessage, "stream", new Stream
                    {
                        Name = Stream.StdOut,
                        Text = string.Join("\r\n", outputs)
                    }), this);
            }
            catch (RuntimeException ex)
            {
                this.lastExecutionSucceeded = false;
                this.IOPubSocket.SendMessage(Message.Create(
                    originalMessage, "stream", new Stream
                    {
                        Name = Stream.StdErr,
                        Text = $"{ex.GetType()}: {ex.Message}\r\n{ex.ErrorRecord.ScriptStackTrace}"
                    }), this);
            }

            this.executionCount += 1;
            this.activePowerShell = null;
            return this.executionCount;
            }
        }

        public void Interrupt()
        {
            try
            {
                this.activePowerShell?.Stop();
            }
            catch
            {
            }
        }


        public CommandCompletion InvokeCommandCompletion(CompleteRequest req)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = this.psRunspace;
            return CommandCompletion.CompleteInput(req.Code, req.CursorPosition, null, ps);
        }

        private void HeartbeatTask()
        {
            var hbAddress = this.GetAddress(this.connection.HBPort);
            Console.WriteLine($"Starting heartbeat handler at {hbAddress}");
            this.HBSocket = new ResponseSocket(hbAddress);

            while (!this.cancellationTokenSource.IsCancellationRequested)
            {
                var msg = new NetMQ.Msg();
                msg.InitEmpty();
                try
                {
                    if (this.HBSocket.TryReceive(ref msg, TimeSpan.FromMilliseconds(100)))
                    {
                        var frame = msg.Data;
                        this.HBSocket.SendFrame(frame);
                    }
                }
                finally
                {
                    msg.Close();
                }
            }

            Console.WriteLine("Stopping heartbeat handler");
            this.HBSocket.Dispose();
        }

        private void ServerTask()
        {
            var shellAddress = this.GetAddress(this.connection.ShellPort);
            var ioAddress = this.GetAddress(this.connection.IOPubPort);
            Console.WriteLine($"Starting shell server at {shellAddress}");
            Console.WriteLine($"Starting iopub publisher at {ioAddress}");
            this.ShellSocket = new RouterSocket(shellAddress);
            this.IOPubSocket = new PublisherSocket(ioAddress);

            var globalSessionId = Guid.NewGuid().ToString();
            this.IOPubSocket.SendMessage(Message.Create(
                globalSessionId, "status", new Status {ExecutionState = Status.Starting}), this);

            this.serverPoller = new NetMQPoller();
            this.ShellSocket.ReceiveReady += (_, _) =>
            {
                if (this.cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                var message = this.ShellSocket.ReceiveMessage(this);
                this.IOPubSocket.SendMessage(Message.Create(
                    message, "status", new Status {ExecutionState = Status.Busy}), this);

                if (!string.IsNullOrEmpty(message.Signature))
                {
                    switch (message.Header.MessageType)
                    {
                        case "kernel_info_request":
                            MessageHandlers.HandleKernelInfo(this, message);
                            break;
                        case "execute_request":
                            MessageHandlers.HandleExecute(this, message);
                            break;
                        case "complete_request":
                            MessageHandlers.HandleComplete(this, message);
                            break;
                        case "is_complete_request":
                            MessageHandlers.HandleIsComplete(this, message);
                            break;
                        case "shutdown_request":
                            MessageHandlers.HandleShutdown(this, message);
                            break;
                        case "interrupt_request":
                            MessageHandlers.HandleInterrupt(this, message);
                            break;
                    }
                }

                this.IOPubSocket.SendMessage(Message.Create(
                    message, "status", new Status {ExecutionState = Status.Idle}), this);
            };

            this.serverPoller.Add(this.ShellSocket);
            this.serverPoller.RunAsync();

            while (!this.cancellationTokenSource.IsCancellationRequested)
            {
                Thread.Sleep(100);
            }

            this.serverPoller.Stop();
            Console.WriteLine("Stopping shell server");
            this.ShellSocket.Dispose();
            Console.WriteLine("Stopping iopub publisher");
            this.IOPubSocket.Dispose();
        }

        private string GetAddress(int port)
        {
            return $"{this.connection.Transport}://{this.connection.IP}:{port}";
        }

        private EventHandler<DataAddedEventArgs> GetStreamHandler(Func<int, string> mapper,
            PowerShell powerShell, string targetStream, Message originalMessage)
        {
            return (sender, args) =>
            {
                this.IOPubSocket.SendMessage(Message.Create(
                    originalMessage, "stream", new Stream
                    {
                        Name = targetStream,
                        Text = mapper(args.Index) + "\r\n"
                    }), this);
            };
        }
    }
}