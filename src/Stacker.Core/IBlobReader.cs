using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Reads source blobs by immutable commit and path for syntax-aware diff rendering.</summary>
/// <remarks>Blob IDs are content-addressed, so highlighted output can be reused for every comparison that shows the same content.
/// The read is bounded and optional: a blob over the adapter's limit, or one that is not present locally, comes back as
/// <see langword="null"/> rather than failing the comparison, because missing highlighting is cosmetic while a missing file list is
/// not.</remarks>
public interface IBlobReader
{
    /// <summary>Returns the blob at one path in one commit, or null when it does not exist there.</summary>
    /// <remarks>An absent path is normal and not an error: an added file has no old-side blob and a deleted file has no new-side
    /// blob, and the caller renders that side unhighlighted. The read is a local object lookup: a partial clone must not start a
    /// network fetch from a renderer, so missing content is reported as absent instead of downloaded.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="commit">The immutable commit ID whose tree is read.</param>
    /// <param name="path">Repository-relative path of the blob at that commit.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The blob id and text, or <see langword="null"/> for an absent or unavailable blob.</returns>
    Task<Result<BlobContent?>> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default);
}
