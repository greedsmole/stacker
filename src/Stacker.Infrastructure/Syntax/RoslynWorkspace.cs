using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Stacker.Core;

namespace Stacker.Infrastructure;

internal static class RoslynWorkspace
{
    internal static bool IsSolution(string path) => HasExtension(path, ".sln") || HasExtension(path, ".slnx");
    internal static bool IsProject(string path) => HasExtension(path, ".csproj");
    internal static bool IsEntry(string path) => IsSolution(path) || IsProject(path);

    internal static async Task OpenAsync(MSBuildWorkspace workspace, string root, string entry, CancellationToken ct)
    {
        var file = Path.GetFullPath(entry, root);
        try
        {
            if (IsProject(file))
                await workspace.OpenProjectAsync(file, cancellationToken: ct);
            else
                await workspace.OpenSolutionAsync(file, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StackerException($"Cannot load {entry}. Check the project's .NET SDK and package sources. {ex.Message}");
        }
    }

    internal static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool HasExtension(string path, string extension) =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
