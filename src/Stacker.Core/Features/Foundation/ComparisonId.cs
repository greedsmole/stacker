using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Identity of one textual comparison between a basis commit and a head commit, including the comparison policy.</summary>
/// <param name="Value">Digest produced by <see cref="ComparisonId.For"/>: 64 lowercase hexadecimal characters.</param>
/// <remarks>The value is content-derived, so the same change, commits and policy always produce the same id. A caller
/// that stores or compares a comparison identity passes <see cref="Value"/> rather than re-deriving it from display
/// text. The session compares selections by value, so a changed id counts as a selection change and invalidates query
/// tickets issued before it.</remarks>
public readonly record struct ComparisonId(string Value)
{
    /// <summary>Derives the comparison id from the change, both commit ids and the policy token.</summary>
    /// <param name="change">Change node id the comparison belongs to.</param>
    /// <param name="basis">Merge-base commit id used as the old side of the comparison.</param>
    /// <param name="head">Head commit id used as the new side.</param>
    /// <param name="policy">Comparison policy token. It participates in the digest, so a future change to how
    /// comparisons are produced yields new ids instead of reusing ids stored for the old policy.</param>
    /// <returns>The derived comparison id.</returns>
    /// <remarks>The default <c>unified-v1</c> names the current textual unified-diff policy and is the only value the
    /// code uses; nothing else reads the string. Why that exact label was chosen is not recorded.</remarks>
    public static ComparisonId For(string change, string basis, string head, string policy = "unified-v1") => new(StableIdentity.For(change, basis, head, policy));
}
