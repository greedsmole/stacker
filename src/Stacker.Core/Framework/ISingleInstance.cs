using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>Detects a second instance of the application so app-data writers never interleave.</summary>
/// <remarks>The application assumes a single writer for its profile: two windows sharing the journal and settings
/// would race even with atomic files. The implementation holds an OS handle for the lifetime of the process, so a
/// crash releases it automatically; the second launch is refused rather than allowed to share state.</remarks>
public interface ISingleInstance
{
    /// <summary>True when this process owns the application; false when another instance already runs.</summary>
    /// <remarks>Repeated calls in the owning process keep returning true; the handle is acquired once and released
    /// when the implementation is disposed.</remarks>
    /// <returns>True when the profile is owned by this process.</returns>
    bool TryAcquire();
}
