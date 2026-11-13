namespace Stacker.Core;

/// <summary>Definitions plus the content revision used to prevent overwriting external edits.</summary>
/// <param name="Stacks">Every definition in the document, in authored order.</param>
/// <param name="Revision">Content hash (uppercase SHA-256 hex) of the bytes that were read, or null when no configuration document exists; it is
/// saved back with the document so a later save can prove it replaces exactly what was read.</param>
public sealed record StackDocument(IReadOnlyList<StackDefinition> Stacks, string? Revision);
