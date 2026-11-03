using System.Text.Json;
using Stacker.Core.Framework;
namespace Stacker.Core;

public enum PublicationPhase { Prepared, Sending, Confirmed, Uncertain, Rejected }
public sealed record PublicationOperation(string Id, string Kind, string ContextIdentity, long PullRequest,
    string BaseSha, string HeadSha, string Payload, PublicationPhase Phase, DateTimeOffset CreatedAt);

/// <summary>Durable review rules independent of a view, selection or Avalonia dispatcher.</summary>
public sealed class ReviewSessionService(IApplicationState store, IGitHubReader reader)
{
    public Task<Result<ReviewDraft?>> LoadAsync(string key, CancellationToken ct = default) => store.ReadAsync<ReviewDraft>("drafts", key, ct);
    public Task<Result<Unit>> SaveAsync(string key, ReviewDraft draft, CancellationToken ct = default) => store.WriteAsync("drafts", key, draft, ct);
    public async Task ValidateAsync(GitHubRepositoryContext context, PullRequest expected, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct = default)
    {
        (await reader.VerifyAccessAsync(context, ct)).OrThrow();
        var current = (await reader.PullRequestAsync(context, expected.Number, ct)).OrThrow();
        if (current.HeadSha != expected.HeadSha || current.BaseSha != expected.BaseSha)
            throw new StackerException("PR base/head changed. Refresh and reopen PR changes. Your draft is preserved.");
        (await reader.ValidateAnchorsAsync(context, current, anchors, ct)).OrThrow();
    }
    public static void Prepare(ReviewDraft draft, string id, string kind, GitHubRepositoryContext context, PullRequest pr)
    {
        if (draft.Operation is { Phase: PublicationPhase.Prepared or PublicationPhase.Sending or PublicationPhase.Uncertain })
            throw new StackerException("Resolve the existing publication before sending again.");
        // Snapshot before assigning Operation: payload never recursively includes itself or later edits.
        var payload = JsonSerializer.Serialize(new { draft.PendingOperation, draft.PendingBody, draft.PendingSummary, draft.PendingReviewComments,
            draft.PendingFilePath, draft.PendingComposerKey, draft.Decision });
        draft.Operation = new(id, kind, context.Identity, pr.Number, pr.BaseSha, pr.HeadSha, payload, PublicationPhase.Prepared, DateTimeOffset.UtcNow);
    }
    public static void Transition(ReviewDraft draft, PublicationPhase phase)
    {
        if (draft.Operation is not { } operation) return; // Legacy uncertain drafts retain their original marker protocol.
        var allowed = (operation.Phase, phase) switch
        {
            (PublicationPhase.Prepared, PublicationPhase.Sending or PublicationPhase.Rejected) => true,
            (PublicationPhase.Sending, PublicationPhase.Confirmed or PublicationPhase.Uncertain) => true,
            (PublicationPhase.Uncertain, PublicationPhase.Confirmed or PublicationPhase.Rejected) => true,
            _ => operation.Phase == phase
        };
        if (!allowed) throw new StackerException("Invalid publication transition.");
        draft.Operation = operation with { Phase = phase };
        if (phase is PublicationPhase.Confirmed or PublicationPhase.Rejected)
        { draft.PublicationHistory.Add(draft.Operation); draft.Operation = null; }
    }
}
