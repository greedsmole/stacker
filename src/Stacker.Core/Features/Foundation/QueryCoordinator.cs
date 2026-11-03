namespace Stacker.Core;

/// <summary>Shares immutable reads. A consumer only cancels its subscription; the last consumer cancels the work.</summary>
public sealed class QueryCoordinator : IDisposable
{
    private sealed class Entry
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Task<object?> Task = null!;
        public int Readers;
    }
    private readonly object _sync=new();
    private readonly Dictionary<(Type,string),Entry> _entries=[];
    private bool _disposed;
    public async Task<T> RunAsync<T>(string key,Func<CancellationToken,Task<T>> read,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        Entry entry;var identity=(typeof(T),key);
        lock(_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(!_entries.TryGetValue(identity,out entry!))
            {
                entry=new(); var created=entry;
                entry.Task=Task.Run<object?>(async()=>await read(created.Cancellation.Token),CancellationToken.None);
                _entries[identity]=entry;
            }
            entry.Readers++;
        }
        try{return (T)(await entry.Task.WaitAsync(ct))!;}
        finally
        {
            lock(_sync)
            {
                if(--entry.Readers==0)
                {
                    _entries.Remove(identity);
                    if(!entry.Task.IsCompleted)entry.Cancellation.Cancel();
                    _=entry.Task.ContinueWith(t=>{_=t.Exception;entry.Cancellation.Dispose();},TaskScheduler.Default);
                }
            }
        }
    }
    public void Dispose()
    {
        lock(_sync){if(_disposed)return;_disposed=true;foreach(var entry in _entries.Values)entry.Cancellation.Cancel();_entries.Clear();}
    }
}
public enum ReadWork { Metadata,Diff,Syntax,Refresh }
public sealed class ReadScheduler : IDisposable
{
    private readonly SemaphoreSlim[] _slots=[new(4,4),new(2,2),new(1,1),new(1,1)];
    public async Task<T> RunAsync<T>(ReadWork work,Func<CancellationToken,Task<T>> read,CancellationToken ct=default)
    {var gate=_slots[(int)work];await gate.WaitAsync(ct);try{return await read(ct);}finally{gate.Release();}}
    public void Dispose(){foreach(var gate in _slots)gate.Dispose();}
}
