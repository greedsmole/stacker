namespace Stacker.Core;

/// <summary>Which comparison a history document shows and, with it, which action can recreate the document.</summary>
/// <remarks>The mode is carried on <see cref="DiffDocument"/> and on every <see cref="NavigationTarget"/> because back and forward must rebuild the same view from the change, version and path alone. Each mode maps to one producer in the history workspace rather than acting as a general-purpose diff filter.</remarks>
public enum DiffDocumentMode
{
    /// <summary>The selected version's own layer diff, from its merge base to its head; the default read-only comparison.</summary>
    Current,
    /// <summary>The version comparison, anchored at the review checkpoint recorded for the selected file instead of the previously observed version.</summary>
    SinceReviewed,
    /// <summary>The difference between the previously observed version and the current one, split into base movement and replayed commits.</summary>
    BetweenVersions,
    /// <summary>One path traced through the stack path, with one section per layer in which the path changed.</summary>
    FileEvolution
}
