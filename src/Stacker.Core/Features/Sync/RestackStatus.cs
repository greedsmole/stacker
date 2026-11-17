namespace Stacker.Core;

/// <summary>How one restack attempt ended.</summary>
public enum RestackStatus
{
    /// <summary>Every planned layer was rewritten.</summary>
    /// <remarks>The original branch is checked out again before this status is returned.</remarks>
    Applied,
    /// <summary>No layer needed a rewrite.</summary>
    NothingToDo,
    /// <summary>The plan was refused before anything moved.</summary>
    /// <remarks>This holds for a refusal raised while planning. A refusal detected in the middle of a run leaves the journal entry describing the layers that already moved, so the entry, not this status, is the source of truth in that case.</remarks>
    Refused,
    /// <summary>An unexpected failure was undone automatically; the repository is as it was.</summary>
    RolledBack,
    /// <summary>A layer did not apply cleanly and the rebase was left in progress for the user to resolve.</summary>
    Conflicted
}
