namespace Stacker.Core;

/// <summary>File metadata returned before an individual patch is requested.</summary>
/// <param name="OldPath">Path in the base commit; equal to <c>NewPath</c> for everything except a rename or copy.</param>
/// <param name="NewPath">Path in the head commit.</param>
/// <param name="Kind">Git's classification for the pair.</param>
/// <param name="Additions">Added line count from Git's numstat, or null when Git reports no counts - binary content. Null is not zero.</param>
/// <param name="Deletions">Removed line count, with the same null-for-binary meaning.</param>
/// <param name="OldMode">Octal file mode in the base commit, or <c>000000</c> when the path does not exist there.</param>
/// <param name="NewMode">Octal file mode in the head commit, or <c>000000</c> when the path does not exist there.</param>
/// <remarks>Kind and modes come from Git's raw name-status output while the counts come from numstat; both describe the same commit pair, so
/// they cannot disagree. Modes are Git's octal strings: <c>100644</c> regular, <c>100755</c> executable, <c>120000</c> symlink and
/// <c>160000</c> submodule pointer. A rename is reported with both paths, and a rename the reader could not detect appears instead as a delete
/// plus an add.</remarks>
public sealed record FileChange(string OldPath, string NewPath, FileChangeKind Kind,
    int? Additions, int? Deletions, string OldMode, string NewMode)
{
    /// <summary>True when Git reported no line counts, which is how binary content is recognized.</summary>
    /// <remarks>The flag is derived rather than stored: a hand-built change with zero counts is not binary, which matters for adapters that
    /// construct metadata without running Git.</remarks>
    public bool IsBinary => Additions is null;
    /// <summary>True when either side is the <c>160000</c> gitlink mode, so the change is a submodule pointer move rather than file text.</summary>
    public bool IsSubmodule => OldMode == "160000" || NewMode == "160000";
    /// <summary>True when both sides exist with different modes, so the change is executable/symlink metadata rather than content.</summary>
    /// <remarks><c>000000</c> is excluded on purpose: an add or delete also differs from the other side, but that is a new or removed path, not a
    /// mode change on an existing one.</remarks>
    public bool ModeChanged => OldMode != NewMode && OldMode != "000000" && NewMode != "000000";
    /// <summary>Old and new path joined with an arrow for a rename or copy, otherwise the single path.</summary>
    public string DisplayPath => OldPath == NewPath ? NewPath : $"{OldPath} → {NewPath}";
    /// <summary>Compact added/removed summary for a file row, or <c>binary</c> when there are no counts.</summary>
    public string Stats => IsBinary ? "binary" : $"+{Additions}  −{Deletions}";
    /// <summary>Short classification for the file header: submodule, a mode change, or the kind name.</summary>
    /// <remarks>The submodule label wins over a mode change because the mode pair alone does not tell a reader that the path is a gitlink.</remarks>
    public string Detail => IsSubmodule ? "Submodule" : ModeChanged ? $"Mode {OldMode} → {NewMode}" : Kind.ToString();
}
