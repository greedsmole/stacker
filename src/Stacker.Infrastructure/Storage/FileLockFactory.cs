using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure.Framework;

/// <summary>Takes locks in application data, so the user's working tree never contains Stacker's bookkeeping.</summary>
/// <param name="directory">Folder that holds one hashed <c>.lock</c> file per identity; normally under application
/// data.</param>
/// <remarks>The open handle is the lock, never the file's existence. The file is created with
/// <c>OpenOrCreate</c> and never deleted, which keeps lock handling free of cleanup races; a crashed process leaves
/// the file but not the handle, so the next start succeeds. The identity is hashed to a fixed-length name, so an
/// arbitrary repository path is safe as one file name and does not appear in the application-data listing.</remarks>
public sealed class FileLockFactory(string directory) : IFileLockFactory
{
    /// <inheritdoc/>
    /// <remarks>Makes one immediate attempt and, on an <c>IOException</c>, retries every 50 ms for at most 40 attempts.
    /// The last attempt does not wait, so the total sleep is 39 × 50 ms = 1.95 s: about two seconds of bounded wait
    /// before a second window is told to retry instead of hanging. Why 40 attempts and 50 ms were chosen is not recorded.
    /// A failure to create the data folder is <see cref="ProblemCodes.StoreWrite"/>; a lock that stays held is
    /// <see cref="ProblemCodes.StoreBusy"/>. Cancellation while waiting propagates. An exception other than a retryable
    /// <c>IOException</c> is treated as busy immediately, and the last attempt's <c>IOException</c> also lands in that
    /// catch-all, so the trailing busy result is effectively unreachable with the current control flow.</remarks>
    public async Task<Result<IFileLock>> TryAcquireAsync(string identity, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) { return Result<IFileLock>.Fail(ProblemCodes.StoreWrite, $"Cannot use the Stacker data folder: {ex.Message}"); }
        var path = System.IO.Path.Combine(directory, Hash(identity) + ".lock");
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                // OpenOrCreate + FileShare.None: the handle is the lock, and it disappears with the process.
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                return Result<IFileLock>.Ok(new HeldFileLock(path, stream));
            }
            catch (IOException) when (attempt < 39) { await Task.Delay(50, ct); }
            catch (Exception ex) { return Result<IFileLock>.Fail(ProblemCodes.StoreBusy, $"Another Stacker window is using this repository: {ex.Message}"); }
        }
        return Result<IFileLock>.Fail(ProblemCodes.StoreBusy, "Another Stacker window is saving this repository. Retry in a moment.");
    }

    /// <summary>Hashes an identity to the lower-case SHA-256 hex name shared by lock files and JSON documents.</summary>
    /// <remarks>A fixed 64-character name makes every identity safe as one file name and hides the original path from
    /// the directory listing. Lower-casing normalizes the upper-case output of <c>Convert.ToHexString</c>; identities
    /// that differ only by case still hash differently because their UTF-8 bytes differ. The same function is exposed
    /// as <see cref="ApplicationStore.Key"/>, so object and history paths use the same naming.</remarks>
    /// <param name="identity">The resource identity, normally a repository root.</param>
    /// <returns>The 64-character lower-case hexadecimal digest.</returns>
    internal static string Hash(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
}
