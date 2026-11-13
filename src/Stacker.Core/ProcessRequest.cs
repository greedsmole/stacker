namespace Stacker.Core;

/// <summary>Complete execution policy for one external process, including resource limits and isolated environment edits.</summary>
/// <param name="Executable">Program to run: a bare name resolved on PATH, or an absolute path.</param>
/// <param name="Arguments">Already-split arguments, passed as separate entries so no shell parses them; an argument with spaces stays one
/// argument.</param>
/// <param name="WorkingDirectory">Working directory for the child, or null for the application's current directory; repository commands pass
/// the repository root explicitly.</param>
/// <param name="Timeout">Upper bound for the whole run; null leaves the budget to the runner, whose fallback is 30 seconds. At the boundary the
/// child process tree is killed and the call fails with a timeout problem.</param>
/// <param name="MaxOutputBytes">Combined cap across stdout and stderr, in bytes: the run fails with <see cref="OutputLimitException"/> once the
/// combined output passes it, so exactly the cap is still accepted. The default is 16 MiB (<c>16 * 1024 * 1024</c>); it bounds what one read
/// may retain and turns an oversized answer into a clear message instead of memory pressure.</param>
/// <param name="Environment">Variables to add or override for the child, applied on top of the inherited environment.</param>
/// <param name="StandardInput">Text written to the child's standard input, which is then closed so a tool waiting for end-of-input proceeds; null
/// closes it immediately.</param>
/// <param name="UnsetEnvironment">Variables to remove from the child's environment after <c>Environment</c> is applied, so a leaked token or
/// redirect can be dropped; null removes nothing.</param>
/// <remarks>One record carries everything a run needs, so a caller cannot forget a limit and an adapter can enforce every bound in one place.
/// An adapter applies the environment additions and removals to the child, writes and closes its input, and enforces the timeout and output cap
/// on the same run.</remarks>
public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory = null,
    TimeSpan? Timeout = null, int MaxOutputBytes = 16 * 1024 * 1024,
    IReadOnlyDictionary<string, string>? Environment = null, string? StandardInput = null, IReadOnlyList<string>? UnsetEnvironment = null);
