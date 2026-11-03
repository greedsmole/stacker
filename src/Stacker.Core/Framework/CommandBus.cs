namespace Stacker.Core.Framework;

/// <summary>One foreground mutation at a time. Reads and ordered draft persistence remain independent.</summary>
public sealed class CommandBus(CommandRunner runner)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _stopping;
    private readonly CancellationTokenSource _shutdown = new();
    public bool IsBusy => _gate.CurrentCount == 0;
    public event Action<CommandOutcome>? Reported;
    public event Action? Starting;

    public async Task<CommandOutcome> RunAsync(RepoRoot repository, IStackCommand command,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => await ExecuteAsync(repository.Value, command, progress, ct);

    public Task<CommandOutcome> RunRemoteAsync(GitHubRepositoryContext context, IStackCommand command, CancellationToken ct = default)
        => ExecuteAsync("github:" + context.Identity, command, null, ct);

    private async Task<CommandOutcome> ExecuteAsync(string scope, IStackCommand command, IProgress<string>? progress, CancellationToken ct)
    {
        if (_stopping || !_gate.Wait(0))
            return Report(CommandOutcome.Refused(_stopping ? "The application is closing." : "An operation is already running."));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        try { Starting?.Invoke(); return Report(await runner.RunAsync(scope, command, progress, linked.Token)); }
        finally { _gate.Release(); }
    }

    private CommandOutcome Report(CommandOutcome outcome)
    {
        Reported?.Invoke(outcome);
        return outcome;
    }

    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        _stopping = true;
        await _shutdown.CancelAsync();
        if (!await _gate.WaitAsync(timeout)) return false;
        _gate.Release(); return true;
    }
}
