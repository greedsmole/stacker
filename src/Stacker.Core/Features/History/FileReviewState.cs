using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>How a file of the current layer stands against the reviewer's last checkpoint for that path.</summary>
public enum FileReviewState
{
    /// <summary>No checkpoint exists for the path, or its recorded version is no longer among the change's stored versions,
    /// so there is no evidence to compare from.</summary>
    Unreviewed,
    /// <summary>The checkpoint was taken on the current version, or the comparison found neither a code change for the path
    /// nor a moved base — the earlier review still covers it.</summary>
    Reviewed,
    /// <summary>The compared range changes this path's content since the checkpoint, so the earlier review no longer covers it.</summary>
    CodeChanged,
    /// <summary>The path itself is unchanged, but the layer's base or merge base moved, so the diff the reviewer saw has a
    /// different context and the file must be re-checked.</summary>
    BaseChanged,
    /// <summary>Some change entry for this path is a rename or a mode change, so the state names an identity move. A rename that
    /// also edits text lands here too, because the reviewer has to look again either way.</summary>
    MetadataChanged,
    /// <summary>The comparison could not be produced — unmaterialized contents, no merge base, or a replay that conflicts —
    /// so no state can be derived and the reviewer must look at the file.</summary>
    Unavailable
}
