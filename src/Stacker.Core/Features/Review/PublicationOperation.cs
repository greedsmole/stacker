using System.Text.Json;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One publication attempt, recorded before the send and kept as evidence after it finishes.</summary>
/// <remarks>
/// The same <see cref="Id"/> is carried by the draft payload, the foreground command, the durable journal entry and the
/// remote evidence row, which is what lets the app decide whether an interrupted send reached GitHub. Only one unfinished
/// operation is allowed per draft, and <see cref="Payload"/> is immutable once written: saving a changed payload under the
/// same id is refused by the store instead of silently re-describing what was sent.
/// </remarks>
/// <param name="Id">Operation id, a compact GUID marker also appended to the published body as evidence.</param>
/// <param name="Kind">Publication kind: <c>comment</c>, <c>file</c>, <c>inline</c>, <c>reply</c>, <c>review</c> or <c>resolve</c>.</param>
/// <param name="ContextIdentity">GitHub context identity at preparation time, so another account or repository cannot adopt the operation.</param>
/// <param name="PullRequest">Pull request number the mutation targets.</param>
/// <param name="BaseSha">Base commit of the pull request version the send was prepared against.</param>
/// <param name="HeadSha">Head commit of the pull request version the send was prepared against.</param>
/// <param name="Payload">Immutable JSON snapshot of the pending text, target file, destination and review decision.</param>
/// <param name="Phase">Phase at the time of the record; only the terminal phases are kept in the draft's history.</param>
/// <param name="CreatedAt">UTC preparation time, used to order the evidence of several attempts.</param>
public sealed record PublicationOperation(string Id, string Kind, string ContextIdentity, long PullRequest,
    string BaseSha, string HeadSha, string Payload, PublicationPhase Phase, DateTimeOffset CreatedAt);
