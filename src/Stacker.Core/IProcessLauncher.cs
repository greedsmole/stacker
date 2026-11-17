using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Starts a program without waiting for it or capturing its output, for editors and other interactive tools.</summary>
/// <remarks>Fire-and-forget by design: an editor stays open for as long as the user wants, so there is no timeout, no output
/// capture and no exit code to report. A program that cannot be started fails the result with a problem the settings dialog can
/// show; everything after a successful start is outside the app's control and outside its recovery model.</remarks>
public interface IProcessLauncher
{
    /// <summary>Launches the process and returns immediately; a program that cannot be started comes back as a failed result.</summary>
    /// <remarks>The started process receives the request's arguments, working directory and environment edits but no redirected
    /// streams, so it keeps running after this call returns and after the app exits. A successful result means "handed to the
    /// operating system", not that the tool did its job, and it never reports the tool's later exit status.</remarks>
    /// <param name="request">The executable, arguments and environment to launch with.</param>
    /// <returns><see cref="Unit"/> on a successful start.</returns>
    Result<Unit> Start(ProcessRequest request);
}
