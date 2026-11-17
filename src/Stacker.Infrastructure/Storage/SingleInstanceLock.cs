using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure.Framework;

/// <summary>One application instance per user: a second launch is refused rather than sharing app data.</summary>
public sealed class SingleInstanceLock(string directory) : ISingleInstance, IDisposable
{
    private FileStream? _stream;
    public bool TryAcquire()
    {
        if (_stream is not null)
            return true;
        try
        {
            Directory.CreateDirectory(directory);
            _stream = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
