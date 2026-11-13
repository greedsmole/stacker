using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Persisted local review work, including unsent text retained across navigation and transient failures.</summary>
/// <remarks>
/// This object is the durability boundary of the review screen. It stores the pull request version the text targets, one
/// buffer per destination, the draft comments and the publication evidence. Version fields are compared with the live
/// pull request before a send, and the pending fields hold the exact snapshot of what was sent so an acknowledgement
/// removes only that text and leaves later edits alone.
/// </remarks>
public sealed class ReviewDraft
{
    /// <summary>Revision of the stored draft document, used to save only over the version this session loaded.</summary>
    /// <remarks>Not serialized: the revision lives in the surrounding storage record, and a repository save with a stale
    /// value fails with <see cref="ProblemCodes.StoreStaleRevision"/> instead of overwriting another window's edits.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public long PersistenceRevision
    {
        get; set;
    }
    /// <summary>GitHub context identity (host, repository id and account) the draft belongs to.</summary>
    public string ContextIdentity { get; set; } = "";
    /// <summary>Pull request number the draft belongs to.</summary>
    public long PullRequest
    {
        get; set;
    }
    /// <summary>Head commit the draft text and comments are anchored to.</summary>
    public string HeadSha { get; set; } = "";
    /// <summary>Base commit the draft text and comments are anchored to.</summary>
    public string BaseSha { get; set; } = "";
    /// <summary>Review summary text, submitted with the review decision.</summary>
    public string Summary { get; set; } = "";
    /// <summary>Legacy pull-request conversation buffer, still mirrored so older drafts and readers keep working.</summary>
    public string Composer { get; set; } = "";
    /// <summary>Format version of the composer fields; zero marks a draft written before per-destination buffers existed.</summary>
    /// <remarks>On load, a zero version moves the old single buffer into <see cref="RecoveredText"/> and sets this field to
    /// one, so pre-upgrade text is preserved but never posted to a destination it was not written for.</remarks>
    public int EditorVersion
    {
        get; set;
    }
    /// <summary>Text recovered from a destination that no longer exists, offered for manual reuse.</summary>
    /// <remarks>A line selection whose anchor targets an older pull request version is moved here when the draft is attached
    /// to a diff section, because that anchor cannot be sent; the text stays available instead of being discarded.</remarks>
    public string RecoveredText { get; set; } = "";
    /// <summary>Unsent text per draft destination, keyed by <see cref="ComposerKey.Encoded"/>.</summary>
    /// <remarks>Separate buffers are what keep one tab, reply or line selection from overwriting another's draft text.</remarks>
    public Dictionary<string, string> ContextComposers { get; set; } = [];
    /// <summary>Destination the in-flight send was written in, cleared together with <see cref="PendingBody"/>.</summary>
    public string? PendingComposerKey
    {
        get; set;
    }
    /// <summary>Body text captured before the pending marker was appended, so acknowledgement does not erase text edited
    /// afterwards.</summary>
    public string? PendingBody
    {
        get; set;
    }
    /// <summary>Review summary captured for the in-flight review submission.</summary>
    public string? PendingSummary
    {
        get; set;
    }
    /// <summary>Draft comments captured for the in-flight review submission.</summary>
    public List<DraftComment>? PendingReviewComments
    {
        get; set;
    }
    /// <summary>Unsent file-comment text per changed file path.</summary>
    public Dictionary<string, string> FileComposers { get; set; } = [];
    /// <summary>File path of the in-flight file comment.</summary>
    public string? PendingFilePath
    {
        get; set;
    }
    /// <summary>Review decision submitted with the draft comments.</summary>
    /// <remarks>One of <c>COMMENT</c>, <c>APPROVE</c> or <c>REQUEST_CHANGES</c>; the GitHub writer refuses any other value
    /// instead of letting the API reject the whole review.</remarks>
    public string Decision { get; set; } = "COMMENT";
    /// <summary>Line comments added to the review but not yet submitted.</summary>
    public List<DraftComment> Comments { get; set; } = [];
    /// <summary>Encoded marker (publication kind and marker, or thread and target state) of a send whose outcome is unknown.</summary>
    /// <remarks>Non-null while sending stays blocked: it is what a reload can match against the GitHub thread, and it is
    /// cleared only when the outcome is resolved, because losing it would make a possibly published comment look unsent.</remarks>
    public string? PendingOperation
    {
        get; set;
    }
    /// <summary>The unfinished publication, or null when no send is pending.</summary>
    public PublicationOperation? Operation
    {
        get; set;
    }
    /// <summary>Completed publications kept as evidence, one entry per confirmed or rejected send.</summary>
    public List<PublicationOperation> PublicationHistory { get; set; } = [];
    /// <summary>Anchors that were replaced when draft comments were reattached to a new version.</summary>
    /// <remarks>The original anchors stay in this history so a reanchored comment can still be traced to the version the
    /// reviewer actually read.</remarks>
    public List<DraftComment> PreviousAnchors { get; set; } = [];
}
