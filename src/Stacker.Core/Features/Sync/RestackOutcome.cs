namespace Stacker.Core;

/// <summary>Result of one restack attempt; a rollback has already restored every ref it touched.</summary>
/// <remarks>The journal entry is the record of what happened: it drives undo, resume and recovery. The status decides what the entry can still do, from undoable after <see cref="RestackStatus.Applied"/> to needing the user after <see cref="RestackStatus.Conflicted"/> and already reversed after <see cref="RestackStatus.RolledBack"/>.</remarks>
/// <param name="Status">How the attempt ended.</param>
/// <param name="ConflictingBranch">Layer that stopped on a conflict, or null when the failure was not a per-layer conflict.</param>
/// <param name="ConflictingFiles">Paths Git reported as unmerged for that layer; empty otherwise.</param>
/// <param name="Message">User-facing account of the attempt, including any restoration that happened.</param>
/// <param name="Journal">Entry recording the attempt; null for a refusal that never opened one, and otherwise the plan and moved steps that undo and resume rely on.</param>
public sealed record RestackOutcome(RestackStatus Status, string? ConflictingBranch, IReadOnlyList<string> ConflictingFiles,
    string Message, Framework.JournalEntry? Journal)
{
    /// <summary>Whether every planned layer was rewritten.</summary>
    public bool Applied => Status == RestackStatus.Applied;
    /// <summary>Whether the run stopped on a conflict and left the rebase in progress.</summary>
    public bool Conflicted => Status == RestackStatus.Conflicted;
    /// <summary>Whether the run was reversed, so no branch should still point at a new commit.</summary>
    public bool RolledBack => Status == RestackStatus.RolledBack;
    /// <summary>The effects that actually moved, which an undo must reverse.</summary>
    /// <remarks>Read from the journal's moved steps, so a planned or failed step is never offered for undo; the value is empty when there is no journal.</remarks>
    public IReadOnlyList<Framework.JournalStep> Steps => Journal?.Moved ?? [];
}
