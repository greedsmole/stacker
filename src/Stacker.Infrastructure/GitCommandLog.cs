using System.Text.RegularExpressions;
using Stacker.Core;

namespace Stacker.Infrastructure;

public sealed record GitCommandEntry(Guid Id, DateTimeOffset StartedAt, string WorkingDirectory, string Command,
    TimeSpan Duration, int? ExitCode, string Detail, bool IsRunning)
{
    public string Summary => $"{StartedAt.LocalDateTime:HH:mm:ss}  {(IsRunning ? "running" : Duration.TotalSeconds.ToString("0.0") + "s")}  {(IsRunning ? "" : ExitCode is { } code ? $"exit {code}" : "failed")}  {Command}";
}

public sealed class GitCommandLog
{
    private readonly object _sync = new();
    private readonly List<GitCommandEntry> _entries = [];
    public event Action<GitCommandEntry?>? Changed;
    public IReadOnlyList<GitCommandEntry> Snapshot()
    { lock (_sync) return _entries.ToArray(); }
    public void Clear() { lock (_sync) _entries.Clear(); Changed?.Invoke(null); }
    internal Guid? Begin(ProcessRequest request, DateTimeOffset started)
    {
        var name = Path.GetFileName(request.Executable);
        if (!name.Equals("git", StringComparison.OrdinalIgnoreCase) && !name.Equals("git.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var command = "git " + string.Join(' ', request.Arguments.Select(Quote));
        var entry = new GitCommandEntry(Guid.NewGuid(), started, request.WorkingDirectory ?? "", Redact(command), TimeSpan.Zero, null, "", true);
        lock (_sync) { _entries.Add(entry); if (_entries.Count > 500) _entries.RemoveAt(0); }
        Changed?.Invoke(entry);
        return entry.Id;
    }
    internal void Finish(Guid? id, TimeSpan elapsed, int? exitCode, string detail)
    {
        if (id is null) return;
        GitCommandEntry entry;
        lock (_sync)
        {
            var index = _entries.FindIndex(item => item.Id == id);
            if (index < 0) return;
            entry = _entries[index] with { Duration = elapsed, ExitCode = exitCode,
                Detail = Redact(detail.Length > 2048 ? detail[..2048] + "…" : detail), IsRunning = false };
            _entries[index] = entry;
        }
        Changed?.Invoke(entry);
    }
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
    private static string Redact(string value) => Regex.Replace(value, @"(?i)(https?://)[^\s/@]+:[^\s/@]+@", "$1[credentials]@");
}
