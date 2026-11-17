using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Defines the process boundary used by Git and GitHub CLI adapters.</summary>
/// <remarks>Keeping arguments separate from the executable prevents shell interpretation and makes cancellation testable.
/// Every port in this file follows one failure contract: an expected failure - a tool that cannot start, a timeout, output over
/// its limit, or a refusal reported by an adapter - comes back as a failed <see cref="Result{T}"/> carrying a catalogued
/// <see cref="Problem"/>, never as an exception crossing the boundary; only cancellation is rethrown as
/// <see cref="OperationCanceledException"/>. <see cref="Result{T}"/> is a reference type on purpose, so an unguarded default is
/// a null reference rather than a silent success, and a successful call always carries a value.</remarks>
public interface IProcessRunner
{
    /// <summary>Runs one bounded process operation and returns both output streams and its exit code.</summary>
    /// <remarks>Both streams are captured and drained concurrently, so a tool that fills one pipe cannot deadlock against a reader
    /// waiting on the other. A nonzero exit code is returned as data in <see cref="ProcessResult"/> rather than as a failed result:
    /// the meaning of an exit code belongs to the adapter that issued the command. The call fails when the process cannot be
    /// started, when combined output passes <see cref="ProcessRequest.MaxOutputBytes"/> (the failure is an
    /// <see cref="OutputLimitException"/>), or when it runs past its timeout; in those cases the child process tree is killed before
    /// the problem is returned. <see cref="ProcessRequest.StandardInput"/> is written and then closed, so a tool that waits for
    /// end-of-input proceeds instead of hanging.</remarks>
    /// <param name="request">The executable, its already-split arguments and the resource bounds for this run.</param>
    /// <param name="ct">Cancels the run and kills the child; cancellation is an exception, not a problem.</param>
    /// <returns>The exit code and the decoded standard output and error streams; an empty stream is an empty string, not null.</returns>
    Task<Result<ProcessResult>> RunAsync(ProcessRequest request, CancellationToken ct = default);
}
