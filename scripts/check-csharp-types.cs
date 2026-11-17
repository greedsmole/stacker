#:package Microsoft.CodeAnalysis.CSharp@5.9.0
#:property PublishAot=false

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

bool valid = true;
foreach (string folder in new[] { "src", "tests", "scripts" })
{
    foreach (string path in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
    {
        if (path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
        {
            continue;
        }

        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
        var types = syntax.GetRoot().DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(member => member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Where(member => !IsPartialContainer(member))
            .ToArray();
        if (types.Length <= 1)
        {
            continue;
        }

        foreach (var type in types)
        {
            var line = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            string name = type switch
            {
                BaseTypeDeclarationSyntax declaration => declaration.Identifier.ValueText,
                DelegateDeclarationSyntax declaration => declaration.Identifier.ValueText,
                _ => throw new InvalidOperationException()
            };
            Console.Error.WriteLine($"{path}:{line}: give {name} its own C# file");
        }

        valid = false;
    }
}

return valid ? 0 : 1;

static bool IsPartialContainer(MemberDeclarationSyntax member)
{
    // A separate file can use a partial shell to preserve a nested type's visibility.
    return member is TypeDeclarationSyntax type
        && type.Modifiers.Any(SyntaxKind.PartialKeyword)
        && type.AttributeLists.Count == 0
        && type.BaseList is null
        && type.ParameterList is null
        && type.ConstraintClauses.Count == 0
        && type.Members.Count > 0
        && type.Members.All(child => child is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
}
