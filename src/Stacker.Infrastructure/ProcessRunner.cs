using System.Diagnostics;
using System.Text;
using Stacker.Core;

namespace Stacker.Infrastructure;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(request.Timeout ?? TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var info = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var arg in request.Arguments) info.ArgumentList.Add(arg);
        if (request.Environment is not null)
            foreach (var (key, value) in request.Environment) info.Environment[key] = value;
        if (request.UnsetEnvironment is not null)
            foreach (var key in request.UnsetEnvironment) info.Environment.Remove(key);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new StackerException($"Cannot start {request.Executable}. Check its installation and path. {ex.Message}"); }
        async Task WriteInputAsync()
        {
            try
            {
                if (request.StandardInput is not null) await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), linked.Token);
                process.StandardInput.Close();
            }
            catch { await linked.CancelAsync(); throw; }
        }
        var total = 0;
        async Task<string> ReadAsync(Stream stream)
        {
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer, linked.Token)) != 0)
                {
                    if (Interlocked.Add(ref total, count) > request.MaxOutputBytes) throw new OutputLimitException();
                    output.Write(buffer, 0, count);
                }
                return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            }
            catch { await linked.CancelAsync(); throw; }
        }
        var stdout = ReadAsync(process.StandardOutput.BaseStream);
        var stderr = ReadAsync(process.StandardError.BaseStream);
        var stdin = WriteInputAsync();
        try
        {
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdout, stderr, stdin);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* The process exited between HasExited and Kill. */ }
            await linked.CancelAsync();
            try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr, stdin); } catch { /* Observe both pipe readers before disposing. */ }
            if (stdout.Exception?.GetBaseException() is OutputLimitException || stderr.Exception?.GetBaseException() is OutputLimitException)
                throw new OutputLimitException();
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new StackerException("Operation timed out. Retry or select a smaller comparison.");
            throw;
        }
    }
}

public sealed class GitExecutable
{
    public string? Override { get; set; }
    public string Resolve()
    {
        if (!string.IsNullOrWhiteSpace(Override)) return Override;
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), name));
        var defaults = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe") }
            : new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git" };
        return paths.Concat(defaults).FirstOrDefault(File.Exists) ?? name;
    }
}
