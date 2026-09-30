namespace Stacker.Desktop.ViewModels;

/// <summary>One delayed read at a time. Activation never performs an immediate request.</summary>
public sealed class RefreshCoordinator(Func<CancellationToken, Task> refresh, TimeProvider? clock = null) : IDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CancellationTokenSource? _loop;
    public TimeSpan Interval { get; private set; } = TimeSpan.FromMinutes(1);
    public bool Suspended { get; private set; }
    public void SetActive(bool active)
    {
        if (!active) { _loop?.Cancel(); _loop = null; return; }
        if (_loop is not null || Suspended) return;
        _loop = new(); _ = RunAsync(_loop);
    }
    public void Reset() { Suspended = false; Interval = TimeSpan.FromMinutes(1); }
    private async Task RunAsync(CancellationTokenSource source)
    {
        try
        {
            while (!source.IsCancellationRequested)
            {
                await Task.Delay(Interval, _clock, source.Token);
                try { await refresh(source.Token); Interval = TimeSpan.FromMinutes(1); }
                catch (OperationCanceledException) when (source.IsCancellationRequested) { return; }
                catch (Stacker.Core.GitHubAuthenticationException) { Suspended = true; return; }
                catch { Interval = TimeSpan.FromMinutes(Math.Min(8, Interval.TotalMinutes * 2)); }
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_loop, source)) _loop = null; source.Dispose(); }
    }
    public void Dispose() => SetActive(false);
}
