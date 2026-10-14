namespace Stacker.Core.Framework;

/// <summary>What a publication that may or may not have reached GitHub was.</summary>
public enum PendingKind { Comment, FileComment, Inline, Reply, Review, Resolve }

/// <summary>
/// A publication whose result is unknown, decoded from the marker persisted with the draft.
/// </summary>
/// <remarks>The encoded form is the file format: <c>kind|marker</c>, or <c>resolve|thread|true|false</c>.</remarks>
public sealed record PendingOperation(PendingKind Kind, string Marker, string? ThreadId = null, bool Resolved = false)
{
    public static PendingOperation For(string kind, string marker) => new(kind switch
    {
        "file" => PendingKind.FileComment,
        "inline" => PendingKind.Inline,
        "reply" => PendingKind.Reply,
        "review" => PendingKind.Review,
        _ => PendingKind.Comment
    }, marker);

    public static PendingOperation ForResolve(string threadId, bool resolved) => new(PendingKind.Resolve, "", threadId, resolved);

    public string Encode() => Kind == PendingKind.Resolve ? $"resolve|{ThreadId}|{Resolved}" : $"{Wire(Kind)}|{Marker}";

    /// <summary>Decodes a persisted marker; a damaged one is reported instead of throwing during a reload.</summary>
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

    private static string Wire(PendingKind kind) => kind switch
    {
        PendingKind.FileComment => "file",
        PendingKind.Inline => "inline",
        PendingKind.Reply => "reply",
        PendingKind.Review => "review",
        _ => "comment"
    };
}
