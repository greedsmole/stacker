namespace Stacker.Core;

/// <summary>One in-flight write and one latest pending snapshot. Flush bypasses the debounce delay.</summary>
public sealed class CoalescingWriter<T>(Func<T,Task> write,TimeSpan? delay=null)
{
    private readonly object _sync=new();
    private readonly SemaphoreSlim _flush=new(0,1);
    private T? _pending;
    private bool _hasPending;
    private bool _running;
    private Task _drain=Task.CompletedTask;
    public int PendingCount { get { lock(_sync)return _hasPending?1:0; } }
    public Task Queue(T snapshot)
    {
        lock(_sync){_pending=snapshot;_hasPending=true;if(!_running){_running=true;_drain=Task.Run(DrainAsync);}return _drain;}
    }
    public async Task FlushAsync()
    {
        Task drain;lock(_sync){if(_flush.CurrentCount==0)_flush.Release();if(!_running&&_hasPending){_running=true;_drain=Task.Run(DrainAsync);}drain=_drain;}
        await drain.ConfigureAwait(false);
    }
    private async Task DrainAsync()
    {
        try{while(true)
        {
            await _flush.WaitAsync(delay??TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
            T snapshot;
            lock(_sync){if(!_hasPending){_running=false;return;}snapshot=_pending!;_pending=default;_hasPending=false;}
            await write(snapshot).ConfigureAwait(false);
            lock(_sync){if(!_hasPending){_running=false;return;}}
        }}catch{lock(_sync)_running=false;throw;}
    }
}
