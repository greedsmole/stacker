using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>Write-ahead record of one command; the only thing that makes an effect recoverable.</summary>
/// <remarks>One entry owns every step of a command and its terminal phase. It is persisted before the first
/// effect and rewritten after each step, so the stored copy is at least as strong as the command's own knowledge.
/// Records are immutable: an update produces a new instance with a later <see cref="UpdatedAt"/>, and the store
/// matches it by <see cref="Id"/>. A damaged or unsupported entry blocks recovery instead of being replaced with
/// an empty history.</remarks>
/// <param name="SchemaVersion">Shape of this entry; the JSON journal accepts 2 through
/// <see cref="CurrentSchema"/> and refuses anything else, so a newer entry is never silently truncated.</param>
/// <param name="Id">Stable operation identifier shared with diagnostics and, for remote work, with the persisted
/// publication payload.</param>
/// <param name="Repository">Scope key the entry belongs to (a local repository scope or a GitHub context identity);
/// it is also the partition used to find the entry again.</param>
/// <param name="Kind">Command kind that produced the entry, such as <c>restack</c>, <c>local.push</c> or
/// <c>github.comment</c>; used for filtering, recovery priority and the summary.</param>
/// <param name="StartedAt">Clock time when the entry was opened; also shown for saved stashes.</param>
/// <param name="UpdatedAt">Clock time of the last persisted change.</param>
/// <param name="Phase">How the command ended; see <see cref="JournalPhase"/>.</param>
/// <param name="Steps">Planned effects in execution order; an update replaces the step with the matching id.</param>
/// <param name="StashRef">Commit id of a stash attached to this entry, kept separate so undo and stash restore do
/// not compete.</param>
/// <param name="PendingStepId">Id of the step the command paused on, normally a conflicting rebase; used to select
/// the layer to resume or reverse.</param>
/// <param name="Message">Last user-facing message; <see cref="Summary"/> derives a line when it is
/// <see langword="null"/>.</param>
public sealed record JournalEntry(int SchemaVersion, string Id, string Repository, string Kind,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, JournalPhase Phase, IReadOnlyList<JournalStep> Steps,
    string? StashRef, string? PendingStepId, string? Message)
{
    /// <summary>The entry schema this build writes. The JSON journal also reads schema 2 and preserves the stored
    /// version when it updates an older entry.</summary>
    public const int CurrentSchema = 3;
    /// <summary>The approved restack plan, lifted from the synthetic <c>original</c> step when the entry is opened;
    /// it lets Continue resume the remaining layers without replanning against a changed repository.</summary>
    public RestackPlan? RestackPlan
    {
        get; init;
    }
    /// <summary>An entry the user must deal with: still running, or stopped for a decision.</summary>
    public bool NeedsAttention => Phase is JournalPhase.Running or JournalPhase.NeedsUser;
    /// <summary>Whether the effects recorded here can still be reversed.</summary>
    /// <remarks>Rolled-back, undone and dismissed entries are final; an applied entry stays undoable because a
    /// deliberate undo is how the user reverses a restack that finished successfully.</remarks>
    public bool CanUndo => Phase is JournalPhase.Running or JournalPhase.Applied or JournalPhase.NeedsUser;
    /// <summary>Completed effects that recorded the commit they replaced, which an undo can use as its candidate
    /// list.</summary>
    /// <remarks>Every completed step that recorded a previous commit, including remote pushes. Callers that rewrite
    /// refs narrow it further: the undo path keeps only <c>rebase</c> steps whose new commit is also known.</remarks>
    public IReadOnlyList<JournalStep> Moved => Steps.Where(step => step.State == StepState.Done && step.OldSha is not null).ToArray();
    /// <summary>
    /// Every intended effect whose original commit is known, including one whose record never landed.
    /// Recovery reverses these, which is what makes a crash between an effect and its record recoverable.
    /// </summary>
    /// <remarks>Includes <see cref="StepState.Done"/>, <see cref="StepState.Started"/> and
    /// <see cref="StepState.Unknown"/> because all three may have changed a ref. Only <c>rebase</c> steps qualify:
    /// the synthetic <c>original</c> step stores a branch name in the commit field, and remote writes cannot be
    /// restored automatically. Recovery still refuses a move unless the current ref equals the recorded new
    /// commit.</remarks>
    public IReadOnlyList<JournalStep> Restorable => Steps.Where(step => step.OldSha is not null && step.Kind == "rebase"
        && step.State is StepState.Done or StepState.Started or StepState.Unknown).ToArray();
    /// <summary>The step identified by <see cref="PendingStepId"/>, or <see langword="null"/> when the entry has no
    /// pending step or the id no longer matches one.</summary>
    public JournalStep? Pending => PendingStepId is null ? null : Steps.FirstOrDefault(step => step.Id == PendingStepId);
    /// <summary>One line for the operations list: the stored message, otherwise the command kind plus the number of
    /// moved effects when any moved.</summary>
    public string Summary => Message ?? (Moved.Count == 0 ? Kind : $"{Kind} · {Moved.Count} effect(s)");
}
