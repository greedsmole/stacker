namespace Stacker.Core;

/// <summary>The bounded result of one process run: exit code and both decoded output streams.</summary>
/// <param name="ExitCode">The exit code as reported; a nonzero value is data, not necessarily a failure, because the meaning belongs to the tool
/// that ran.</param>
/// <param name="StdOut">Standard output decoded as UTF-8; empty when the tool wrote nothing.</param>
/// <param name="StdErr">Standard error decoded as UTF-8; empty when the tool wrote nothing.</param>
/// <remarks>Both streams share the request's output cap and are drained concurrently, so a tool that fills one pipe cannot deadlock the read.
/// An empty stream is an empty string, never null.</remarks>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
