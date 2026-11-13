namespace Stacker.Core;

/// <summary>Work classes that each have their own read concurrency gate, in the order of the scheduler's slot array.</summary>
/// <remarks>The numeric value is the index into <see cref="ReadScheduler"/>'s gate array, so reordering or inserting a
/// member silently moves every later class to a different limit. No code in <c>src</c> currently selects the syntax or
/// refresh slot; they are reserved for the highlighting and background-refresh paths that the architecture record
/// assigns one slot each.</remarks>
public enum ReadWork
{
    /// <summary>GitHub metadata and API reads; at most 4 run at once.</summary>
    Metadata,
    /// <summary>Patch reads from the repository; at most 2 run at once.</summary>
    Diff,
    /// <summary>Syntax highlighting work; a single read at a time.</summary>
    Syntax,
    /// <summary>Background refresh work; a single read at a time.</summary>
    Refresh
}
