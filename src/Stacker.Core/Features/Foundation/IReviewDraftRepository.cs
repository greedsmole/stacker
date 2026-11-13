using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Persists review drafts with an explicit revision, so two windows cannot silently interleave saves.</summary>
/// <remarks>A read returns a null draft for a key that was never saved rather than an empty payload. A write compares
/// the expected revision inside one transaction; a mismatch fails and leaves the stored draft untouched, and the caller
/// keeps its own text in the window instead of losing it.</remarks>
public interface IReviewDraftRepository
{
    /// <summary>Reads the draft stored under <paramref name="key"/>.</summary>
    /// <param name="key">Draft key, usually one review context.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The draft and its revision, or a null value when nothing is stored yet.</returns>
    Task<Result<RevisionedDraft?>> ReadDraftAsync(string key, CancellationToken ct = default);
    /// <summary>Saves a draft only when the stored revision still matches the revision the caller read.</summary>
    /// <param name="key">Draft key.</param>
    /// <param name="draft">Complete payload to store.</param>
    /// <param name="expectedRevision">Revision the caller read, or 0 to insert a draft that does not exist yet.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>The revision the payload was stored as: the expected revision plus one.</returns>
    /// <remarks>A first save inserts instead of replacing, so if another window inserted the same key in the meantime
    /// the insert affects no row and the caller gets the stale-revision failure rather than overwriting that draft.
    /// The failure is how the UI can say that text is kept in this window and must be copied before reloading.</remarks>
    Task<Result<long>> SaveDraftAsync(string key, ReviewDraft draft, long expectedRevision, CancellationToken ct = default);
}
