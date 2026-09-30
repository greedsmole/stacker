// Purpose: Coordinates background refresh timing and avoids overlapping or stale refresh work.
namespace Stacker.Desktop.ViewModels;

/// <summary>One delayed read at a time. Activation never performs an immediate request.</summary>
public sealed class RefreshCoordinator(Func<CancellationToken, Task> refresh, TimeProvider? clock = null) : IDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CancellationTokenSource? _loop;
    public TimeSpan Interval { get; private set; } = TimeSpan.FromMinutes(1);
    public bool Suspended { get; private set; }
    /// <summary>Starts one polling loop while active, or cancels it when the window is inactive.</summary>
    public void SetActive(bool active)
    {
        if (!active) { _loop?.Cancel(); _loop = null; return; }
        if (_loop is not null || Suspended) return;
        _loop = new(); _ = RunAsync(_loop);
    }
    public void Reset() { Suspended = false; Interval = TimeSpan.FromMinutes(1); }
    // One loop owns the timer and awaits each refresh, so slow GHES requests never overlap with another poll.
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
