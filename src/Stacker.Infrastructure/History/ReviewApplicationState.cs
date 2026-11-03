using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Compatibility application-state facade. Draft revisions travel with the draft, not an adapter-owned map.</summary>
public sealed class ReviewApplicationState(ApplicationStore legacy, SqliteHistoryStore history) : IApplicationState, IReviewDraftRepository
{
    public string Root => legacy.Root;
    public string? LastProblem => legacy.LastProblem;
    public Task<Result<RevisionedDraft?>> ReadDraftAsync(string key,CancellationToken ct=default)=>history.ReadDraftAsync(key,ct);
    public Task<Result<long>> SaveDraftAsync(string key,ReviewDraft draft,long expectedRevision,CancellationToken ct=default)=>history.SaveDraftAsync(key,draft,expectedRevision,ct);
    public async Task<Result<T?>> ReadAsync<T>(string kind,string identity,CancellationToken ct=default) where T:class
    {
        if(kind!="drafts")return await ((IApplicationState)legacy).ReadAsync<T>(kind,identity,ct);
        var read=await ReadDraftAsync(identity,ct);
        if(read.IsFailed)return Result<T?>.Fail(read.Problem!);
        if(read.Value is null)return Result<T?>.Ok(null);
        read.Value.Draft.PersistenceRevision=read.Value.Revision;
        return Result<T?>.Ok(read.Value.Draft as T);
    }
    public async Task<Result<Unit>> WriteAsync<T>(string kind,string identity,T value,CancellationToken ct=default) where T:class
    {
        if(kind!="drafts")return await ((IApplicationState)legacy).WriteAsync(kind,identity,value,ct);
        if(value is not ReviewDraft draft)return Result<Unit>.Fail(ProblemCodes.StateInvalid,"Draft state requires a typed review draft.");
        var saved=await SaveDraftAsync(identity,draft,draft.PersistenceRevision,ct);
        if(saved.IsFailed)return Result<Unit>.Fail(saved.Problem!);
        draft.PersistenceRevision=saved.Value;return Result.Ok;
    }
}
