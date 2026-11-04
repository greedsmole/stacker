namespace Stacker.Core.Framework;

/// <summary>One foreground mutation at a time. Reads and ordered draft persistence remain independent.</summary>
public sealed class CommandBus(CommandRunner runner, IDiagnosticSink? diagnostics = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private bool _stopping;
    private bool _paused;
    private CancellationTokenSource? _active;
    private readonly CancellationTokenSource _shutdown = new();
    public bool IsBusy => _gate.CurrentCount == 0;
    public event Action<CommandOutcome>? Reported;
    public event Action? Starting;

    public async Task<CommandOutcome> RunAsync(RepoRoot repository, IStackCommand command,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => await ExecuteAsync(repository.Value, command, progress, ct);

    public Task<CommandOutcome> RunRemoteAsync(GitHubRepositoryContext context, IStackCommand command, CancellationToken ct = default)
        => ExecuteAsync("github:" + context.Identity, command, null, ct);

    public Result<IReadOnlyList<JournalEntry>> Recent(string scope)=>runner.Recent(scope);
    private async Task<CommandOutcome> ExecuteAsync(string scope, IStackCommand command, IProgress<string>? progress, CancellationToken ct)
    {
        var started=TimeProvider.System.GetTimestamp();
        using var correlation = OperationCorrelation.Enter(OperationCorrelation.Current ?? Guid.NewGuid().ToString("N"));
        CancellationTokenSource? linked=null;string? refusal=null;
        lock(_sync)
        {
            if(_stopping||_paused)refusal="The workspace is stopping operations.";
            else if(!_gate.Wait(0))refusal="An operation is already running.";
            else _active=linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_shutdown.Token);
        }
        if(refusal is not null)return Report(CommandOutcome.Refused(refusal) with{Scope=scope});
        using(linked)
        {
            try { Starting?.Invoke(); return Report((await runner.RunAsync(scope, command, progress, linked!.Token)) with { Scope = scope },TimeProvider.System.GetElapsedTime(started).TotalMilliseconds); }
            finally { lock(_sync){_active=null;_gate.Release();} }
        }
    }

    private CommandOutcome Report(CommandOutcome outcome,double durationMs=0)
    {
        outcome = outcome with { OperationId = OperationCorrelation.Current };
        diagnostics?.Record(new(outcome.OperationId ?? "", outcome.Status.ToString(), outcome.Problem?.Code, durationMs));
        Reported?.Invoke(outcome);
        return outcome;
    }

    public Task<bool> PauseAsync(TimeSpan timeout)=>QuiesceAsync(timeout,false);
    public void Resume(){lock(_sync){if(!_stopping)_paused=false;}}
    public Task<bool> StopAsync(TimeSpan timeout)=>QuiesceAsync(timeout,true);
    private async Task<bool> QuiesceAsync(TimeSpan timeout,bool permanent)
    {
        var started=TimeProvider.System.GetTimestamp();Task cancellation;
        lock(_sync)
        {
            _paused=true;if(permanent)_stopping=true;
            cancellation=permanent?_shutdown.CancelAsync():_active?.CancelAsync()??Task.CompletedTask;
        }
        try{await cancellation.WaitAsync(timeout);}catch(TimeoutException){return false;}
        var remaining=timeout-TimeProvider.System.GetElapsedTime(started);
        if(remaining<=TimeSpan.Zero||!await _gate.WaitAsync(remaining))return false;
        _gate.Release();return true;
    }
}
