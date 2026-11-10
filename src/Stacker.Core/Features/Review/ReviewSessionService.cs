using System.Text.Json;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Lifecycle of one publication, from the persisted payload to the final recorded outcome.</summary>
/// <remarks>
/// The numeric value is part of the stored format: the draft store treats <c>Prepared</c>, <c>Sending</c> and
/// <c>Uncertain</c> as unfinished, keeps <c>Confirmed</c> sticky, and closes the matching remote journal entry from the
/// terminal value. Reordering or renumbering these members would change the meaning of existing evidence, including
/// operations that were interrupted before an upgrade.
/// </remarks>
public enum PublicationPhase
{
    /// <summary>Payload and operation are persisted but nothing was dispatched; a restart safely rejects it and allows a retry.</summary>
    Prepared,
    /// <summary>The command reached the bus and the process is waiting for GitHub; an exception or restart from this phase is uncertain, not failed.</summary>
    Sending,
    /// <summary>GitHub accepted the mutation. Terminal, and never downgraded by a later save.</summary>
    Confirmed,
    /// <summary>The outcome is unknown because the response was lost or the app stopped mid-send; sending stays blocked and nothing is retried automatically.</summary>
    Uncertain,
    /// <summary>The mutation certainly did not happen, or the user resolved uncertainty after checking GitHub. Terminal, and the unsent text stays available.</summary>
    Rejected
}
/// <summary>One publication attempt, recorded before the send and kept as evidence after it finishes.</summary>
/// <remarks>
/// The same <see cref="Id"/> is carried by the draft payload, the foreground command, the durable journal entry and the
/// remote evidence row, which is what lets the app decide whether an interrupted send reached GitHub. Only one unfinished
/// operation is allowed per draft, and <see cref="Payload"/> is immutable once written: saving a changed payload under the
/// same id is refused by the store instead of silently re-describing what was sent.
/// </remarks>
/// <param name="Id">Operation id, a compact GUID marker also appended to the published body as evidence.</param>
/// <param name="Kind">Publication kind: <c>comment</c>, <c>file</c>, <c>inline</c>, <c>reply</c>, <c>review</c> or <c>resolve</c>.</param>
/// <param name="ContextIdentity">GitHub context identity at preparation time, so another account or repository cannot adopt the operation.</param>
/// <param name="PullRequest">Pull request number the mutation targets.</param>
/// <param name="BaseSha">Base commit of the pull request version the send was prepared against.</param>
/// <param name="HeadSha">Head commit of the pull request version the send was prepared against.</param>
/// <param name="Payload">Immutable JSON snapshot of the pending text, target file, destination and review decision.</param>
/// <param name="Phase">Phase at the time of the record; only the terminal phases are kept in the draft's history.</param>
/// <param name="CreatedAt">UTC preparation time, used to order the evidence of several attempts.</param>
public sealed record PublicationOperation(string Id, string Kind, string ContextIdentity, long PullRequest,
    string BaseSha, string HeadSha, string Payload, PublicationPhase Phase, DateTimeOffset CreatedAt);

/// <summary>Durable review rules independent of a view, selection or Avalonia dispatcher.</summary>
/// <remarks>
/// This service owns two things the views must not do themselves: it decides whether a draft may be sent against the live
/// pull request, and it owns the publication phase machine and its persistence. The foreground command bus still performs
/// the GitHub mutation; this service only prepares its evidence, moves the phase and refuses invalid transitions. Drafts
/// are stored with an optimistic revision so two windows cannot overwrite each other's text.
/// </remarks>
/// <param name="store">Application state. When it also implements <see cref="IReviewDraftRepository"/> the revisioned draft
/// store is used; otherwise drafts fall back to the legacy application key/value storage.</param>
/// <param name="reader">Read-only GitHub port used to revalidate the account and pull request version before a send.</param>
public sealed class ReviewSessionService(IApplicationState store, IGitHubReader reader)
{
    /// <summary>Revision last loaded or written, used as the expected revision of the next draft save.</summary>
    /// <remarks>Zero means "no document yet"; a save on a stale revision fails instead of overwriting text another window
    /// wrote since this one loaded. Only the revisioned repository uses it — the legacy store has no revision.</remarks>
    private long _revision;
    /// <summary>Loads the draft for a context/PR key and repairs publication phases left behind by an interrupted send.</summary>
    /// <remarks>
    /// Key shape is the GitHub context identity plus the pull request number, so one account's draft is never shown for
    /// another account on the same machine. With the revisioned store the revision is captured for later saves; a read
    /// failure is returned rather than replaced with an empty draft, because presenting an empty draft would invite the
    /// user to retype text that still exists. An interrupted phase is normalized before the draft is returned:
    /// <c>Sending</c> becomes <c>Uncertain</c> because the command may have reached GitHub, while <c>Prepared</c> becomes
    /// <c>Rejected</c> and clears the pending markers because nothing was dispatched, so the user may retry.
    /// </remarks>
    /// <param name="key">Context identity and pull request number identifying the draft.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The stored draft, or null when none exists; a failed read is propagated.</returns>
    public async Task<Result<ReviewDraft?>> LoadAsync(string key, CancellationToken ct = default)
    {
        if (store is not IReviewDraftRepository drafts)
        {
            var legacy = await store.ReadAsync<ReviewDraft>("drafts", key, ct);
            NormalizeInterrupted(legacy.Value);
            return legacy;
        }
        var result = await drafts.ReadDraftAsync(key, ct);
        if (result.IsFailed) return Result<ReviewDraft?>.Fail(result.Problem!);
        _revision = result.Value?.Revision ?? 0;
        NormalizeInterrupted(result.Value?.Draft);
        return Result<ReviewDraft?>.Ok(result.Value?.Draft);
    }
    /// <summary>Repairs the phase of a publication that was interrupted by a restart.</summary>
    /// <remarks>
    /// A <c>Sending</c> operation may or may not have reached GitHub, so it is downgraded to <c>Uncertain</c> and the draft
    /// stays blocked until the user resolves it. A <c>Prepared</c> operation was persisted before dispatch, so it certainly
    /// did not happen; rejecting it and dropping the pending markers keeps the draft loadable and sendable without asking
    /// the user to reconcile a send that never left the machine. Legacy drafts without an operation are left untouched and
    /// keep their original marker protocol.
    /// </remarks>
    /// <param name="draft">Stored draft to repair in place, or null when nothing was stored.</param>
    private static void NormalizeInterrupted(ReviewDraft? draft)
    {
        if (draft?.Operation?.Phase == PublicationPhase.Sending) Transition(draft, PublicationPhase.Uncertain);
        else if (draft?.Operation?.Phase == PublicationPhase.Prepared)
        { Transition(draft, PublicationPhase.Rejected); draft.PendingOperation = null; draft.PendingFilePath = null; }
    }
    /// <summary>Persists the draft and advances the revision used by the next save.</summary>
    /// <remarks>
    /// The revisioned store rejects a stale revision, a changed operation payload and a changed operation identity, and that
    /// refusal is returned to the caller rather than hidden: the window keeps its text and reports that the draft changed
    /// elsewhere. The revision is advanced only after a successful write, so a retry uses the revision that is actually
    /// stored. The legacy fallback writes the whole draft without a revision check, which is last-writer-wins.
    /// </remarks>
    /// <param name="key">Context identity and pull request number identifying the draft.</param>
    /// <param name="draft">Draft to store, including its publication fields.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns><see cref="Unit"/> when stored; otherwise the store's problem.</returns>
    public async Task<Result<Unit>> SaveAsync(string key, ReviewDraft draft, CancellationToken ct = default)
    {
        if (store is not IReviewDraftRepository drafts) return await store.WriteAsync("drafts", key, draft, ct);
        var result = await drafts.SaveDraftAsync(key, draft, _revision, ct);
        if (result.IsFailed) return Result<Unit>.Fail(result.Problem!);
        _revision = result.Value; return Result.Ok;
    }
    /// <summary>Refuses to publish unless the account, the pull request version and every anchor still match the server.</summary>
    /// <remarks>
    /// Order matters. The account and repository are verified first, because publishing under a switched account would
    /// attach the review to the wrong identity. The live pull request is read next and both commit ids are compared with the
    /// snapshot the draft was written against; a mismatch throws so the caller can tell the user to refresh and reopen the
    /// changes, because a comment anchored to an older head would land on a diff GitHub no longer shows or, worse, on a
    /// different line. Anchors are validated last against the server's own diff, which also re-checks each anchor's own
    /// commit pair and line range: a review assembled from a mix of old and new anchors is refused even when the draft's
    /// top-level version fields look current. The message is user-facing and the draft is untouched: the text survives
    /// because the refusal happens before anything is sent.
    /// </remarks>
    /// <param name="context">GitHub context whose account and repository are revalidated.</param>
    /// <param name="expected">Snapshot the draft was written against; its number, base SHA and head SHA are compared.</param>
    /// <param name="anchors">Anchors to verify against the live diff; an empty list checks only the version.</param>
    /// <param name="ct">Cancels the verification reads.</param>
    /// <exception cref="StackerException">The account changed, the pull request is closed, the version moved, or an anchor
    /// is no longer present on its side of the diff.</exception>
    public async Task ValidateAsync(GitHubRepositoryContext context, PullRequest expected, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct = default)
    {
        (await reader.VerifyAccessAsync(context, ct)).OrThrow();
        var current = (await reader.PullRequestAsync(context, expected.Number, ct)).OrThrow();
        if (current.HeadSha != expected.HeadSha || current.BaseSha != expected.BaseSha)
            throw new StackerException("PR base/head changed. Refresh and reopen PR changes. Your draft is preserved.");
        (await reader.ValidateAnchorsAsync(context, current, anchors, ct)).OrThrow();
    }
    /// <summary>Records the immutable payload of the next publication and marks it prepared.</summary>
    /// <remarks>
    /// A draft with an unfinished operation is refused here rather than at the view, because two overlapping sends would
    /// leave one set of pending markers describing only one mutation: an acknowledgement could then clear text that was not
    /// sent, or claim the wrong outcome. The payload is serialized before <see cref="ReviewDraft.Operation"/> is assigned so
    /// it never contains itself, and so it captures the text as it was at preparation time instead of following later edits;
    /// that is what makes the operation evidence of what was actually sent. Only the pending fields belong in the payload,
    /// not the whole draft, so saving other edits during the send cannot change the operation identity.
    /// </remarks>
    /// <param name="draft">Draft that will receive the operation and its payload snapshot.</param>
    /// <param name="id">Operation id shared with the command, journal and remote evidence.</param>
    /// <param name="kind">Publication kind, matching the command that will be dispatched.</param>
    /// <param name="context">Context recorded with the operation to make it non-transferable to another account.</param>
    /// <param name="pr">Pull request version the send is prepared against.</param>
    /// <exception cref="StackerException">An operation is already prepared, sending or uncertain.</exception>
    public static void Prepare(ReviewDraft draft, string id, string kind, GitHubRepositoryContext context, PullRequest pr)
    {
        if (draft.Operation is { Phase: PublicationPhase.Prepared or PublicationPhase.Sending or PublicationPhase.Uncertain })
            throw new StackerException("Resolve the existing publication before sending again.");
        // Snapshot before assigning Operation: payload never recursively includes itself or later edits.
        var payload = JsonSerializer.Serialize(new
        {
            draft.PendingOperation,
            draft.PendingBody,
            draft.PendingSummary,
            draft.PendingReviewComments,
            draft.PendingFilePath,
            draft.PendingComposerKey,
            draft.Decision
        });
        draft.Operation = new(id, kind, context.Identity, pr.Number, pr.BaseSha, pr.HeadSha, payload, PublicationPhase.Prepared, DateTimeOffset.UtcNow);
    }
    /// <summary>Moves the unfinished operation to another phase and archives it once it is terminal.</summary>
    /// <remarks>
    /// The allowed moves mirror what the evidence can support: a prepared send may only start or be refused; a sending send
    /// may be confirmed, become uncertain, or be refused when the failure proves nothing was sent; an uncertain send may be
    /// confirmed or rejected after the user checks GitHub. Repeating the current phase is allowed because a reloaded draft
    /// can reapply the phase it already has, and that must not throw. Anything else is a programming error: accepting it
    /// would let a send be confirmed without dispatch or leave the markers describing the wrong operation. A confirmed or
    /// rejected operation is appended to the history and cleared from the draft, so only one unfinished operation remains,
    /// and the stored terminal record is what lets the store close the matching remote journal entry as applied or
    /// dismissed. Legacy drafts without an operation return unchanged instead of throwing.
    /// </remarks>
    /// <param name="draft">Draft whose operation is moved.</param>
    /// <param name="phase">Target phase.</param>
    /// <exception cref="StackerException">The move is not one the phase machine allows.</exception>
    public static void Transition(ReviewDraft draft, PublicationPhase phase)
    {
        if (draft.Operation is not { } operation) return; // Legacy uncertain drafts retain their original marker protocol.
        var allowed = (operation.Phase, phase) switch
        {
            (PublicationPhase.Prepared, PublicationPhase.Sending or PublicationPhase.Rejected) => true,
            (PublicationPhase.Sending, PublicationPhase.Confirmed or PublicationPhase.Uncertain or PublicationPhase.Rejected) => true,
            (PublicationPhase.Uncertain, PublicationPhase.Confirmed or PublicationPhase.Rejected) => true,
            _ => operation.Phase == phase
        };
        if (!allowed) throw new StackerException("Invalid publication transition.");
        draft.Operation = operation with { Phase = phase };
        if (phase is PublicationPhase.Confirmed or PublicationPhase.Rejected)
        { draft.PublicationHistory.Add(draft.Operation); draft.Operation = null; }
    }
}
