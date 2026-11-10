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

/// <summary>
/// A publication whose result is unknown, decoded from the marker persisted with the draft.
/// </summary>
/// <remarks>The encoded form is the file format: <c>kind|marker</c>, or <c>resolve|threadId|True</c> for a resolve.
/// The format has no escaping, so the marker and thread id must not contain the <c>|</c> delimiter; markers are
/// generated as GUIDs without hyphens, which satisfies that. The boolean is written in the runtime's
/// <c>True</c>/<c>False</c> form and parsed case-insensitively. A marker is evidence that a write was attempted, not
/// proof that it landed; reconciliation checks GitHub and only then clears the draft state.</remarks>
/// <param name="Kind">Which publication the marker describes; decides how a reload checks GitHub.</param>
/// <param name="Marker">The random identity stamped into the published body and searched for on reload. Empty for a
/// resolve, whose evidence is the thread state.</param>
/// <param name="ThreadId">Thread id for a resolve marker; ignored for every other kind.</param>
/// <param name="Resolved">Target resolved state for a resolve marker; ignored for every other kind.</param>
public sealed record PendingOperation(PendingKind Kind, string Marker, string? ThreadId = null, bool Resolved = false)
{
    /// <summary>Builds a body-marker operation from the wire name used by the publication commands.</summary>
    /// <remarks>An unknown name falls back to <see cref="PendingKind.Comment"/>, matching the encoder's default for
    /// callers that have a wire name but no type. The persisted parser is stricter and rejects an unknown wire name
    /// before calling this factory, so a damaged marker is never silently reclassified.</remarks>
    /// <param name="kind">Wire name: <c>comment</c>, <c>file</c>, <c>inline</c>, <c>reply</c> or <c>review</c>.</param>
    /// <param name="marker">The marker embedded in the published body.</param>
    /// <returns>A comment-like operation carrying the marker.</returns>
    public static PendingOperation For(string kind, string marker) => new(kind switch
    {
        "file" => PendingKind.FileComment,
        "inline" => PendingKind.Inline,
        "reply" => PendingKind.Reply,
        "review" => PendingKind.Review,
        _ => PendingKind.Comment
    }, marker);

    /// <summary>Builds a resolve marker; no body marker is used because the thread's state is the evidence.</summary>
    /// <param name="threadId">Id of the review thread to resolve or unresolve.</param>
    /// <param name="resolved">The state the thread should reach.</param>
    /// <returns>A resolve operation with an empty marker.</returns>
    public static PendingOperation ForResolve(string threadId, bool resolved) => new(PendingKind.Resolve, "", threadId, resolved);

    /// <summary>Serializes the operation to the exact text persisted with the draft.</summary>
    /// <remarks>Resolve uses its three-field form; every other kind writes the wire name and marker. The result is
    /// consumed only by <see cref="TryParse"/>, so the delimiter assumption above applies on both sides.</remarks>
    /// <returns>The persisted marker text.</returns>
    public string Encode() => Kind == PendingKind.Resolve ? $"resolve|{ThreadId}|{Resolved}" : $"{Wire(Kind)}|{Marker}";

    /// <summary>Decodes a persisted marker; a damaged one is reported instead of throwing during a reload.</summary>
    /// <remarks>Whitespace-only input, an unknown kind, a missing marker or thread id, an unparseable boolean or the
    /// wrong field count all return false. This is deliberate: the value comes from a draft saved by a previous run,
    /// and a reload must surface "check GitHub" rather than crash the window.</remarks>
    /// <param name="text">The persisted marker text, possibly damaged or absent.</param>
    /// <param name="value">The decoded operation, or a default comment marker when parsing fails.</param>
    /// <returns>True when the text was a complete, well-formed marker.</returns>
    public static bool TryParse(string? text, out PendingOperation value)
    {
        value = new PendingOperation(PendingKind.Comment, "");
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('|');
        if (parts.Length == 3 && parts[0] == "resolve")
        {
            if (parts[1].Length == 0 || !bool.TryParse(parts[2], out var resolved)) return false;
            value = ForResolve(parts[1], resolved);
            return true;
        }
        if (parts.Length != 2 || parts[1].Length == 0) return false;
        if (parts[0] is not ("comment" or "file" or "inline" or "reply" or "review")) return false;
        value = For(parts[0], parts[1]);
        return true;
    }

    /// <summary>Maps a kind to its persisted wire name; unknown kinds fall back to <c>comment</c>.</summary>
    /// <remarks><see cref="PendingKind.Resolve"/> never reaches this helper because <see cref="Encode"/> writes its
    /// own three-field form.</remarks>
    /// <param name="kind">The kind to map.</param>
    /// <returns>The single word stored in the draft.</returns>
    private static string Wire(PendingKind kind) => kind switch
    {
        PendingKind.FileComment => "file",
        PendingKind.Inline => "inline",
        PendingKind.Reply => "reply",
        PendingKind.Review => "review",
        _ => "comment"
    };
}
