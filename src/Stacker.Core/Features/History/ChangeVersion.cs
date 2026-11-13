using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>An immutable observation of one change at one base/head pair; every diff and comparison starts from it.</summary>
/// <remarks>Identity is content, not time: the store derives <see cref="Id"/> from the change and the two commits, so
/// observing the same pair twice returns the stored version, while a force-push creates a new version instead of rewriting
/// the old one. That is what lets a stored graph keep analysing the inputs a review began with. <see cref="MergeBase"/> is
/// deliberately outside the identity: it is evidence that arrives later, when the commits are actually materialized, so a
/// version can be listed without being diffable.</remarks>
/// <param name="Id">Identity derived from <see cref="ChangeId"/> plus both commits; the store recomputes it, so a
/// caller-supplied value acts as an idempotency key rather than as an authority. Core composes <c>change|base|head</c> and
/// hashes it, and the store derives its row key the same way, so that composition — separator included — is part of the
/// identity and must change on both sides together.</param>
/// <param name="ChangeId">Identity of the owning <see cref="ChangeNode"/>.</param>
/// <param name="BaseSha">Commit the layer was based on when observed — the parent ref tip locally, the pull request's base
/// SHA remotely. It is the intended base, not necessarily the merge base.</param>
/// <param name="HeadSha">Tip commit of the layer.</param>
/// <param name="MergeBase">Best common ancestor of <see cref="BaseSha"/> and <see cref="HeadSha"/>, recorded once the
/// commits exist. <see langword="null"/> before materialization or when Git reports zero or several ancestors; comparison
/// and analysis refuse such a version instead of guessing an ancestor.</param>
/// <param name="ObservedAt">When this pair was seen — the GitHub snapshot timestamp remotely, the local clock locally; it is
/// ordering information, not identity.</param>
public sealed record ChangeVersion(string Id, string ChangeId, string BaseSha, string HeadSha, string? MergeBase,
    DateTimeOffset ObservedAt)
{
    /// <summary>Compact label of local observation time and the first eight head characters, short enough for one version row.</summary>
    /// <remarks>The slice is clamped with <c>Math.Min</c> so a truncated or empty SHA cannot throw; eight hexadecimal
    /// characters are a reading convenience, not a uniqueness guarantee.</remarks>
    public string Label => $"{ObservedAt.LocalDateTime:g} · {HeadSha[..Math.Min(8, HeadSha.Length)]}";
    /// <summary>Binds the label wherever the version is shown as text.</summary>
    public override string ToString() => Label;
}
