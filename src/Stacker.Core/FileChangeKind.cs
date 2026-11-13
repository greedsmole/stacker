namespace Stacker.Core;

/// <summary>Git's file-level change classification, including renames and mode changes.</summary>
/// <remarks>Derived from Git's name-status letter for one path pair. A path can exist on both sides while its content type changes, which is why
/// <see cref="TypeChanged"/> is distinct from add and delete.</remarks>
public enum FileChangeKind
{
    /// <summary>The path exists only in the new commit.</summary>
    Added,
    /// <summary>The path exists in both commits with different content.</summary>
    Modified,
    /// <summary>The path exists only in the old commit.</summary>
    Deleted,
    /// <summary>The path moved; <c>OldPath</c> and <c>NewPath</c> differ.</summary>
    Renamed,
    /// <summary>The content was copied to a new path while the old path remains.</summary>
    Copied,
    /// <summary>The path's object type changed, for example a regular file became a symlink or a submodule.</summary>
    TypeChanged
}
