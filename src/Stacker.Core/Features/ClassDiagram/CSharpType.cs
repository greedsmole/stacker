namespace Stacker.Core;

public sealed record CSharpType(string Id, string Project, string Namespace, string Name, string Kind,
    string DeclarationHash, IReadOnlyList<SymbolLocation> Locations, IReadOnlyList<CSharpMember> Members);
