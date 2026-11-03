using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Routes drafts to transactional storage; each scope owns its expected revisions.</summary>
public sealed class ReviewApplicationState(ApplicationStore legacy, SqliteHistoryStore history) : IApplicationState
{
    private readonly Dictionary<string, long> _revisions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Root => legacy.Root;
    private string? _lastProblem;
    public string? LastProblem => _lastProblem ?? legacy.LastProblem;
    public async Task<Result<T?>> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class
    {
        if (kind != "drafts") return await ((IApplicationState)legacy).ReadAsync<T>(kind, identity, ct);
        await _gate.WaitAsync(ct);
        try
        {
            var read = await history.ReadDraftAsync<T>(identity, Path.Combine(Root, kind, ApplicationStore.Key(identity) + ".json"), ct);
            if (read.IsFailed) { _lastProblem = read.Problem!.Message; return Result<T?>.Fail(read.Problem); }
            _revisions[identity] = read.Value.Revision; return Result<T?>.Ok(read.Value.Value);
        }
        finally { _gate.Release(); }
    }
    public async Task<Result<Unit>> WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class
    {
        if (kind != "drafts") return await ((IApplicationState)legacy).WriteAsync(kind, identity, value, ct);
        await _gate.WaitAsync(ct);
        try
        {
            var saved = await history.SaveDraftAsync(identity, value, _revisions.GetValueOrDefault(identity), ct);
            if (saved.IsFailed) { _lastProblem = saved.Problem!.Message; return Result<Unit>.Fail(saved.Problem); }
            _revisions[identity] = saved.Value; return Result.Ok;
        }
        finally { _gate.Release(); }
    }
}
