using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Stacker.Core;

namespace Stacker.Infrastructure;

internal sealed partial class RoslynGraphSymbols
{
    internal IEnumerable<CSharpRelationship> ReadRelationships(IReadOnlySet<string> types, CancellationToken ct)
    {
        var owners = _declarations.Select(item => Id(item.Type)!).ToHashSet(StringComparer.Ordinal);
        foreach (var (type, members) in _declarations)
        {
            ct.ThrowIfCancellationRequested();
            if (type.BaseType is { SpecialType: SpecialType.None } basis)
                yield return Edge(type, null, basis, null, CSharpRelationshipKind.Inherits, types);
            foreach (var contract in type.Interfaces)
                yield return Edge(type, null, contract, null, CSharpRelationshipKind.Implements, types);

            foreach (var member in members)
                foreach (var dependency in MemberTypes(member).SelectMany(NamedTypes))
                    yield return Edge(type, Id(member), dependency, null, CSharpRelationshipKind.UsesType, types);
        }

        foreach (var tree in project.Compilation.SyntaxTrees.Where(tree => SourcePath(tree) is not null))
        {
            var model = project.Compilation.GetSemanticModel(tree);
            var syntax = tree.GetRoot(ct);
            foreach (var node in syntax.DescendantNodes())
            {
                ct.ThrowIfCancellationRequested();
                var kind = node switch
                {
                    BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax or AttributeSyntax => CSharpRelationshipKind.Creates,
                    InvocationExpressionSyntax or BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                        or AssignmentExpressionSyntax or CastExpressionSyntax => CSharpRelationshipKind.Calls,
                    _ => (CSharpRelationshipKind?)null
                };
                if (kind is null || model.GetSymbolInfo(node, ct).Symbol is not IMethodSymbol target)
                    continue;
                var source = EnclosingDeclaration(model, node, ct);
                var owner = source as INamedTypeSymbol ?? source?.ContainingType;
                if (owner is null || Id(owner) is not { } ownerId || !owners.Contains(ownerId))
                    continue;
                yield return Edge(owner, source is INamedTypeSymbol ? null : Id(source!), target.ContainingType, target, kind.Value, types);
            }
        }
    }

    private static ISymbol? EnclosingDeclaration(SemanticModel model, SyntaxNode node, CancellationToken ct)
    {
        // Declaration syntax supplies the owner of initializers and attributes, which can bind outside the member body.
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is not (MemberDeclarationSyntax or VariableDeclaratorSyntax or AccessorDeclarationSyntax))
                continue;
            var symbol = model.GetDeclaredSymbol(ancestor, ct);
            if (symbol is IMethodSymbol { AssociatedSymbol: { } propertyOrEvent })
                return propertyOrEvent;
            if (symbol is ILocalSymbol)
                continue;
            if (symbol is not null)
                return symbol;
        }
        return null;
    }

    private CSharpRelationship Edge(INamedTypeSymbol source, string? sourceMember, INamedTypeSymbol target,
        ISymbol? targetMember, CSharpRelationshipKind kind, IReadOnlySet<string> types)
    {
        var candidate = Id(target);
        var indexed = candidate is not null && types.Contains(candidate);
        return new(Id(source)!, sourceMember, indexed ? candidate : null, indexed && targetMember is not null ? Id(targetMember) : null,
            (targetMember is null ? Definition(target) : Definition(targetMember)).ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), kind);
    }

    private static IEnumerable<ITypeSymbol> MemberTypes(ISymbol member) => member switch
    {
        IFieldSymbol field => [field.Type],
        IEventSymbol @event => [@event.Type],
        IPropertySymbol property => [property.Type, .. property.Parameters.Select(parameter => parameter.Type)],
        IMethodSymbol method => [method.ReturnType, .. method.Parameters.Select(parameter => parameter.Type),
            .. method.TypeParameters.SelectMany(parameter => parameter.ConstraintTypes)],
        _ => []
    };

    private static IEnumerable<INamedTypeSymbol> NamedTypes(ITypeSymbol symbol)
    {
        switch (symbol)
        {
            case INamedTypeSymbol { SpecialType: not SpecialType.System_Void, TypeKind: not TypeKind.Error } named:
                yield return named;
                foreach (var argument in named.TypeArguments.SelectMany(NamedTypes))
                    yield return argument;
                break;
            case IArrayTypeSymbol array:
                foreach (var element in NamedTypes(array.ElementType))
                    yield return element;
                break;
            case IPointerTypeSymbol pointer:
                foreach (var element in NamedTypes(pointer.PointedAtType))
                    yield return element;
                break;
            case IFunctionPointerTypeSymbol function:
                foreach (var element in MemberTypes(function.Signature).SelectMany(NamedTypes))
                    yield return element;
                break;
        }
    }
}
