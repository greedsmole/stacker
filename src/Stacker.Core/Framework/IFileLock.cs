using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>A held exclusive lock; the operating system releases it when the process dies.</summary>
/// <remarks>The open handle is the lock, not the file: a crash closes the handle, so no stale marker can block the
/// next start, and the file's continued existence after a clean release means nothing. Dispose the value to release
/// the lock. Locks live in application data rather than the working tree, so a repository never carries Stacker's
/// bookkeeping.</remarks>
public interface IFileLock : IAsyncDisposable
{
    /// <summary>The lock file path, exposed for diagnostics. The path identifies the lock, but its presence does not
    /// prove that the lock is held.</summary>
    string Path
    {
        get;
    }
}
