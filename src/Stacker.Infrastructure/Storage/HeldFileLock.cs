using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure.Framework;

/// <summary>An exclusive lock that the operating system releases when the process ends.</summary>
/// <param name="path">Lock file path retained for diagnostics; the file's presence does not prove ownership.</param>
/// <param name="stream">The open handle whose sharing mode is the lock; disposing it releases the lock.</param>
/// <remarks>The handle, not a marker file, is the lock. A crash closes the handle, so the next start can take the
/// lock even though the file remains; a clean disposal leaves that file behind as well. A leftover lock file therefore
/// never blocks a future run, and only a held handle refuses a second writer.</remarks>
public sealed class HeldFileLock(string path, FileStream stream) : IFileLock
{
    /// <inheritdoc/>
    public string Path { get; } = path;
    /// <summary>Disposes the held stream, which releases the operating-system lock.</summary>
    /// <returns>A completed task; disposal itself is synchronous.</returns>
    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
