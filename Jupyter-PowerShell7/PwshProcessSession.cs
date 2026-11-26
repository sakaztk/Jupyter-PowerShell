using System.Diagnostics;
using System.Text;

namespace Jupyter_PowerShell7;

public sealed class PwshProcessSession : IDisposable
{
    private readonly string executable;
    private readonly SemaphoreSlim executionLock = new(1, 1);
    private Process process;

public Process Process => this.process;

    public PwshProcessSession(string executablePath = null)
    {
        this.executable = ResolveExecutable(executablePath);

        this.process = StartProcess(this.executable);
    }

    private Process StartProcess(string executable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "-NoLogo -NoProfile -NoExit -Command -",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start PowerShell 7: {executable}");
        }

        return process;
    }

    public async Task<(string StandardOutput, string StandardError)> ExecuteAsync(string script, CancellationToken cancellationToken = default)
    {
        await this.executionLock.WaitAsync(cancellationToken);
        try
        {
        if (this.process.HasExited)
        {
            throw new InvalidOperationException("The PowerShell 7 process has exited.");
        }

        var marker = $"__JUPYTER_PWSH_DONE_{Guid.NewGuid():N}__";
        var encodedScript = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));

        await this.process.StandardInput.WriteLineAsync(
            $"[Console]::InputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $OutputEncoding = [Text.UTF8Encoding]::new($false); $__jupyterScript=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedScript}')); & ([ScriptBlock]::Create($__jupyterScript)) *>&1 | Out-String | ForEach-Object {{ [Console]::Out.Write($_) }}; [Console]::Out.WriteLine('{marker}')",
            cancellationToken);
        await this.process.StandardInput.FlushAsync(cancellationToken);

        var output = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await ReadUntilMarkerAsync(this.process.StandardOutput, marker, output, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("PowerShell execution exceeded the 10 minute execution timeout.");
        }

        return (output.ToString(), string.Empty);
        }
        finally
        {
            this.executionLock.Release();
        }
    }

    public void Interrupt()
    {
        this.Stop();
        this.process.Dispose();
        this.process = StartProcess(this.executable);
    }

    public void Stop()
    {
        if (!this.process.HasExited)
        {
            this.process.StandardInput.Close();
            this.process.WaitForExit(5000);
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
            }
        }
    }

    public void Dispose()
    {
        this.Stop();
        this.process.Dispose();
        this.executionLock.Dispose();
    }

    public bool HasExited => this.process.HasExited;

    public int ExitCode => this.process.HasExited ? this.process.ExitCode : 0;

    private static string ResolveExecutable(string executablePath)
    {
        var explicitCandidates = new[]
        {
            executablePath,
            Environment.GetEnvironmentVariable("POWERSHELL7_PATH")
        };

        foreach (var candidate in explicitCandidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var resolved = ResolveNamedExecutable(candidate!);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(programFiles))
        {
            var installedPwsh = Directory.GetDirectories(Path.Combine(programFiles, "PowerShell"), "*", SearchOption.TopDirectoryOnly)
                .Select(directory => Path.Combine(directory, "pwsh.exe"))
                .Where(File.Exists)
                .Select(path => new
                {
                    Path = path,
                    Version = FileVersionInfo.GetVersionInfo(path).FileVersion
                })
                .Where(item => item.Version is not null)
                .OrderByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Path)
                .FirstOrDefault();

            if (installedPwsh is not null)
            {
                return installedPwsh;
            }
        }

        var pathResult = ResolveNamedExecutable("pwsh.exe");
        if (pathResult is not null)
        {
            return pathResult;
        }

        throw new InvalidOperationException("Could not find PowerShell 7 (pwsh.exe). Pass its path as the second kernel argument or set POWERSHELL7_PATH.");
    }

    private static string ResolveNamedExecutable(string candidate)
    {
        if (Path.IsPathFullyQualified(candidate))
        {
            return File.Exists(candidate) ? candidate : null;
        }

        if (!candidate.Contains(Path.DirectorySeparatorChar) && !candidate.Contains(Path.AltDirectorySeparatorChar))
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var resolved = Path.Combine(directory.Trim(), candidate);
                if (File.Exists(resolved))
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    private static async Task ReadUntilMarkerAsync(StreamReader reader, string marker, StringBuilder output, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            if (line == marker)
            {
                return;
            }

            output.AppendLine(line);
        }
    }
}
