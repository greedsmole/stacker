namespace Stacker.Core;

/// <summary>User-authored ordered description of a stack; branch names are full Git refs.</summary>
/// <param name="Id">Stable identifier used as the configuration key and as the workspace group key; it is never shown and must be
/// unique among the stacks of one repository, which <see cref="StackValidation"/> enforces.</param>
/// <param name="Name">Display name of the stack group.</param>
/// <param name="Base">Full ref of the boundary the lowest layer sits on, normally <c>refs/heads/main</c>; validated on save, resolved
/// when the stack is opened.</param>
/// <param name="Branches">Layer refs in bottom-up order: the first layer sits directly on <c>Base</c> and every later layer sits on the
/// branch before it. Order is the stack itself, so reordering is a definition change, not a display preference.</param>
public sealed record StackDefinition(string Id, string Name, string Base, IReadOnlyList<string> Branches);
