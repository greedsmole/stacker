namespace Stacker.Core;

/// <summary>Unfinished operation the repository is in the middle of.</summary>
/// <remarks>Detected from the presence of Git's state entries rather than from the exit code of a command; the order here follows the detection priority. <see cref="WorkingOperation.Am"/> is separated from <see cref="WorkingOperation.Rebase"/> because <c>git am</c> shares the apply directory with the rebase apply backend and only its <c>applying</c> marker tells them apart.</remarks>
public enum WorkingOperation
{
    /// <summary>No operation is in progress.</summary>
    None,
    /// <summary>A rebase stopped and is waiting for the user to resolve, continue, skip or abort it.</summary>
    Rebase,
    /// <summary>A patch series applied with <c>git am</c> stopped before its next patch.</summary>
    Am,
    /// <summary>A merge stopped before its merge commit was created.</summary>
    Merge,
    /// <summary>A cherry-pick stopped before its commit was created.</summary>
    CherryPick,
    /// <summary>A revert stopped before its commit was created.</summary>
    Revert
}
