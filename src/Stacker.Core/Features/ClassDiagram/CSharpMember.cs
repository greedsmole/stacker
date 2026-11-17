namespace Stacker.Core;

public sealed record CSharpMember(string Id, string Signature, string DeclarationHash, IReadOnlyList<SymbolLocation> Locations);
