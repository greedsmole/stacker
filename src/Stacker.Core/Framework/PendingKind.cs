namespace Stacker.Core.Framework;

/// <summary>What a publication that may or may not have reached GitHub was.</summary>
/// <remarks>The kind decides how a reload proves that the publication landed: comments, reviews, replies and inline
/// comments are found by a marker embedded in their body, while a resolve is checked through the thread's current
/// state. The wire names are persisted inside drafts, so changing <see cref="PendingOperation.Encode"/> or
/// <see cref="PendingOperation.TryParse"/> without accepting the old names would strand saved drafts.</remarks>
public enum PendingKind
{
    /// <summary>A top-level comment on the pull request.</summary>
    Comment,
    /// <summary>A file-level comment attached to a path rather than to a diff line.</summary>
    FileComment,
    /// <summary>A review comment anchored to a line of the diff.</summary>
    Inline,
    /// <summary>A reply to an existing review-comment thread.</summary>
    Reply,
    /// <summary>A submitted review: the summary plus its queued line comments.</summary>
    Review,
    /// <summary>A resolve or unresolve action on a review thread; the thread state is the only evidence.</summary>
    Resolve
}
