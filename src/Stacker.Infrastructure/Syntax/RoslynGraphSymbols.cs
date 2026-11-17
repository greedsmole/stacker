using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Stacker.Core;

namespace Stacker.Infrastructure;

internal sealed partial class RoslynGraphSymbols(string root, IReadOnlySet<string> tracked, GraphProject project,
    ILookup<AssemblyIdentity, GraphProject> assemblies)
{
    private static readonly SymbolDisplayFormat MemberFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeExplicitInterface | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
    private readonly List<(INamedTypeSymbol Type, ISymbol[] Members)> _declarations = [];
    private readonly Dictionary<IAssemblySymbol, string?> _assemblyPaths = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<SyntaxTree, string?> _sourcePaths = new();

    internal IEnumerable<CSharpType> ReadTypes(CancellationToken ct)
    {
        var names = new Stack<INamespaceOrTypeSymbol>();
        names.Push(project.Compilation.Assembly.GlobalNamespace);
        while (names.TryPop(out var container))
        {
            ct.ThrowIfCancellationRequested();
            if (container is INamespaceSymbol space)
                foreach (var child in space.GetMembers())
                    names.Push(child);
            if (container is not INamedTypeSymbol type)
                continue;

            foreach (var nested in type.GetTypeMembers())
                names.Push(nested);
            var locations = Locations(type);
            if (locations.Length == 0 || type.IsImplicitlyDeclared || type.GetDocumentationCommentId() is null)
                continue;

            var members = type.GetMembers().Where(member => member is not INamedTypeSymbol
                && member.GetDocumentationCommentId() is not null
                && (member is not IMethodSymbol method || method.AssociatedSymbol is null && method.PartialDefinitionPart is null))
                .Where(member => Locations(member).Length > 0).ToArray();
            _declarations.Add((type, members));
            var display = type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
            yield return new(Id(type)!, project.Path, type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
                display, type.IsRecord ? $"record {type.TypeKind.ToString().ToLowerInvariant()}" : type.TypeKind.ToString().ToLowerInvariant(),
                Hash(type, ct), locations, members.Select(member => new CSharpMember(Id(member)!, member.ToDisplayString(MemberFormat),
                    Hash(member, ct), Locations(member))).ToImmutableArray());
        }
    }

    private static ISymbol Definition(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { ReducedFrom: { } extension } => Definition(extension),
        IMethodSymbol { PartialDefinitionPart: { } definition } => definition.OriginalDefinition,
        _ => symbol.OriginalDefinition
    };

    private string? Id(ISymbol symbol)
    {
        var definition = Definition(symbol);
        var assembly = definition.ContainingAssembly;
        if (assembly is null || definition.GetDocumentationCommentId() is not { } name)
            return null;
        if (!_assemblyPaths.TryGetValue(assembly, out var path))
        {
            path = SymbolEqualityComparer.Default.Equals(assembly, project.Compilation.Assembly) ? project.Path
                : assemblies[assembly.Identity].Where(candidate => project.Reachable.Contains(candidate.Project.Id))
                    .Select(candidate => candidate.Path).Distinct(StringComparer.Ordinal).Take(2).ToArray() is [var unique] ? unique : null;
            _assemblyPaths[assembly] = path;
        }
        if (path is null)
            return null;

        // File-local types can share a source declaration name in one project.
        var owner = definition as INamedTypeSymbol ?? definition.ContainingType;
        while (owner is not null)
        {
            if (owner.IsFileLocal && owner.Locations.FirstOrDefault()?.SourceTree is { } tree)
                return path + "\0" + SourcePath(tree) + "\0" + name;
            owner = owner.ContainingType;
        }
        return path + "\0" + name;
    }

    private ImmutableArray<SymbolLocation> Locations(ISymbol symbol)
    {
        var locations = symbol.Locations;
        if (symbol is IMethodSymbol { PartialImplementationPart: { } implementation })
            locations = locations.AddRange(implementation.Locations);
        return locations.Where(location => location.SourceTree is { } tree && SourcePath(tree) is not null)
            .Select(location =>
            {
                var position = location.GetLineSpan().StartLinePosition;
                return new SymbolLocation(SourcePath(location.SourceTree!)!, position.Line + 1, position.Character);
            }).Distinct().ToImmutableArray();
    }

    private string? SourcePath(SyntaxTree tree)
    {
        if (!_sourcePaths.TryGetValue(tree, out var path))
        {
            path = Path.IsPathFullyQualified(tree.FilePath) ? RoslynWorkspace.RelativePath(root, tree.FilePath) : null;
            if (path is not null && !tracked.Contains(path))
                path = null;
            _sourcePaths.Add(tree, path);
        }
        return path;
    }

    private string Hash(ISymbol symbol, CancellationToken ct)
    {
        var declarations = symbol.DeclaringSyntaxReferences;
        if (symbol is IMethodSymbol { PartialImplementationPart: { } implementation })
            declarations = declarations.AddRange(implementation.DeclaringSyntaxReferences);
        return string.Join(",", declarations.Where(reference => SourcePath(reference.SyntaxTree) is not null)
            .Select(reference => HashDeclaration(reference.GetSyntax(ct))).Order(StringComparer.Ordinal));
    }

    private static string HashDeclaration(SyntaxNode node)
    {
        // The symbol for a field points at its variable. Include shared type, modifiers and attributes.
        IEnumerable<SyntaxToken> tokens = node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field }
            ? field.AttributeLists.SelectMany(attrs => attrs.DescendantTokens())
                .Concat(field.Modifiers).Concat(field.Declaration.Type.DescendantTokens()).Concat(node.DescendantTokens())
            : node.DescendantTokens(descendIntoChildren: child => child == node || node is not (TypeDeclarationSyntax or EnumDeclarationSyntax)
                || child is not (MemberDeclarationSyntax or EnumMemberDeclarationSyntax));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var token in tokens)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(token.Text));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
