namespace Stacker.Core;

/// <summary>Requests one or more layer comparisons against a consistent repository and stack snapshot.</summary>
/// <param name="Repository">Snapshot that supplies the root and refs; both parents and layers are resolved from it, never from fresh reads.</param>
/// <param name="Stack">Definition whose <c>Branches</c> are the layers.</param>
/// <param name="Mode">Which comparisons are wanted; it overrides or normalizes <c>Positions</c>.</param>
/// <param name="Positions">Zero-based layer indexes: ignored by <c>FullStack</c>, reduced to the highest for <c>Layer</c> and <c>Cumulative</c>,
/// and every distinct value honored for <c>MultiLayer</c>.</param>
public sealed record DiffRequest(RepositorySnapshot Repository, StackDefinition Stack, DiffMode Mode, IReadOnlyList<int> Positions);
