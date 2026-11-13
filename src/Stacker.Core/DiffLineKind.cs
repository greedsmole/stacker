namespace Stacker.Core;

/// <summary>Semantic role of a rendered patch line; line numbers are null on the side where it does not exist.</summary>
public enum DiffLineKind
{
    /// <summary>An unchanged line present on both sides, with both line numbers.</summary>
    Context,
    /// <summary>A line only in the new side, with a new-side line number.</summary>
    Added,
    /// <summary>A line only in the old side, with an old-side line number.</summary>
    Removed,
    /// <summary>A hunk header row; it carries no line number and is never selectable for a comment.</summary>
    Header,
    /// <summary>A patch line that is neither a hunk header nor part of a hunk body, such as a file-mode line or the no-final-newline marker.
    /// It has no line numbers, and the renderer hides all of them except the newline marker.</summary>
    Notice
}
