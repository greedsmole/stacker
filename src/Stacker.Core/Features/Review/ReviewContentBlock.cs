using System.Text;
using System.Text.RegularExpressions;

namespace Stacker.Core;

/// <summary>One text or suggested-replacement block in a review comment, in its original order.</summary>
/// <remarks>
/// A comment is parsed into blocks so the views can render prose as Markdown and each suggestion as a copyable
/// replacement, and so publishing code can tell whether a comment actually proposes a code change. The order is the
/// author's order: a comment may mix several suggestions with prose before, between and after them.
/// </remarks>
/// <param name="Text">Block content. For a suggestion this is the replacement code as written between the fences, with
/// LF line breaks and its indentation preserved; for a text block it is the original Markdown.</param>
/// <param name="IsSuggestion">True when the block came from a recognized suggestion fence rather than prose.</param>
/// <param name="LinesAbove">GitLab-style count of unchanged lines above the replacement; zero for a plain GitHub
/// suggestion, which replaces only the targeted lines.</param>
/// <param name="LinesBelow">GitLab-style count of unchanged lines below the replacement; zero for a plain suggestion.</param>
public sealed record ReviewContentBlock(string Text, bool IsSuggestion, int LinesAbove = 0, int LinesBelow = 0)
{
    /// <summary>True for a prose block, which the view renders as selectable text instead of a replacement panel.</summary>
    public bool IsText => !IsSuggestion;
    /// <summary>Caption shown for the block, including the offset counts when the author used the GitLab form.</summary>
    public string Label => LinesAbove == 0 && LinesBelow == 0 ? "Suggested change" : $"Suggested change · −{LinesAbove} / +{LinesBelow} lines";
    /// <summary>True for a suggestion whose replacement is empty, which proposes deleting the targeted lines.</summary>
    public bool IsDeletion => IsSuggestion && Text.Length == 0;
}
