namespace Stacker.Core;

public sealed record CSharpRelationship(string SourceType, string? SourceMember, string? TargetType, string? TargetMember,
    string TargetSignature, CSharpRelationshipKind Kind);
