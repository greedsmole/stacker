using Stacker.Core.Framework;
namespace Stacker.Core;

public interface IApplicationState
{
    string Root { get; }
    string? LastProblem { get; }
    Task<Result<T?>> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class;
    Task<Result<Unit>> WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class;
}
public interface IGitToolSettings { string? Override { get; set; } string Resolve(); }
public interface ISettingsDiagnostics { string? LastProblem { get; } }
public interface IGhToolSettings { string? Override { get; set; } bool UseSavedCredentials { get; set; } }
public interface IDemoRepository { Task<Result<string>> CreateAsync(CancellationToken ct = default); }
public interface IDemoSnapshotReader { Task<Result<GitHubSnapshot?>> ReadDemoSnapshotAsync(string root, CancellationToken ct = default); }
public interface IGitDiagnosticLog
{
    string? GitExecutablePath { get; set; }
    event Action<GitCommandEntry?>? Changed;
    IReadOnlyList<GitCommandEntry> Snapshot();
    void Clear();
}

public sealed record GitCommandEntry(Guid Id, DateTimeOffset StartedAt, string WorkingDirectory, string Command,
    TimeSpan Duration, int? ExitCode, string Detail, bool IsRunning)
{
    public string Summary => $"{StartedAt.LocalDateTime:HH:mm:ss}  {(IsRunning ? "running" : Duration.TotalSeconds.ToString("0.0") + "s")}  {(IsRunning ? "" : ExitCode is { } code ? $"exit {code}" : "failed")}  {Command}";
}

/// <summary>Explicit transient state for previews/tests without an OS-owned application store.</summary>
public sealed class MemoryApplicationState : IApplicationState
{
    private readonly Dictionary<(string, string), string> _values = [];
    public string Root => "transient";
    public string? LastProblem => null;
    public Task<Result<T?>> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class =>
        Task.FromResult(Result<T?>.Ok(_values.TryGetValue((kind, identity), out var json) ? System.Text.Json.JsonSerializer.Deserialize<T>(json) : null));
    public Task<Result<Unit>> WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class
    { _values[(kind, identity)] = System.Text.Json.JsonSerializer.Serialize(value); return Task.FromResult(Result.Ok); }
}
