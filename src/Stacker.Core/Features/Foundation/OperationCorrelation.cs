using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Ambient operation id that ties one user action's bus run, journal entry and diagnostics together.</summary>
/// <remarks>The value lives in an <see cref="AsyncLocal{T}"/>, so it flows into awaited continuations and thread-pool
/// work started inside the scope while two parallel operations keep separate ids. A caller enters a scope with a known
/// id when one is already persisted — a prepared publication payload keeps its id through the send — and lets the
/// first participant mint one when none exists.</remarks>
public static partial class OperationCorrelation
{
    /// <summary>The ambient value, tracked per async flow; null when no operation scope is active.</summary>
    private static readonly AsyncLocal<string?> Active = new();
    /// <summary>The id of the innermost active operation, or <see langword="null"/> outside one.</summary>
    public static string? Current => Active.Value;
    /// <summary>Makes <paramref name="id"/> the current operation id until the returned scope is disposed.</summary>
    /// <param name="id">Id to use. Passing the id that is already current is harmless and keeps it on exit.</param>
    /// <returns>A scope that restores the previous id, which makes nesting and reuse safe.</returns>
    /// <remarks>Consumers enter it with a <c>using</c> declaration. Because the id is ambient instead of a parameter, a
    /// Git read or journal write deep in the call chain can stamp the same id without changing its signature.</remarks>
    public static IDisposable Enter(string id)
    {
        var previous = Active.Value;
        Active.Value = id;
        return new Scope(() => Active.Value = previous);
    }
}
