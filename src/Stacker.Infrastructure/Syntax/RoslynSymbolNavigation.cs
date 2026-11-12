using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

public sealed class RoslynSymbolNavigation(GitRepositoryReader git) : ISymbolNavigation
{
    private const int MaxResults = 500;

    public Task<Result<IReadOnlyList<string>>> ProjectsAsync(SymbolNavigationRequest request, CancellationToken ct) =>
        Boundary.CaptureAsync<IReadOnlyList<string>>(async () =>
        {
            await git.RequireCleanCheckoutAsync(request.Root, request.Commit, ct);
            var paths = await git.TrackedPathsAsync(request.Root, request.Commit, ct);
            var solutions = paths.Where(path => HasExtension(path, ".sln") || HasExtension(path, ".slnx"))
                .Order(StringComparer.Ordinal).ToArray();
            if (solutions.Length > 0) return solutions;

            var projects = paths.Where(path => HasExtension(path, ".csproj"))
                .Order(StringComparer.Ordinal).ToArray();
            var directory = Path.GetDirectoryName(request.Path);
            while (!string.IsNullOrEmpty(directory))
            {
                var prefix = directory.Replace('\\', '/') + "/";
                var local = projects.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)
                    && !path.AsSpan(prefix.Length).Contains('/')).ToArray();
                if (local.Length > 0) return local;
                directory = Path.GetDirectoryName(directory);
            }
            return projects.Length > 0 ? projects : throw new StackerException("This checkout has no C# solution or project.");
        }, ProblemCodes.GitFailed);

    public Task<Result<SymbolNavigationResult>> FindAsync(SymbolNavigationRequest request, string project, CancellationToken ct) =>
        Boundary.CaptureAsync(() => Task.Run(() => FindCoreAsync(request, project, ct), ct), ProblemCodes.StateInvalid);

    private async Task<SymbolNavigationResult> FindCoreAsync(SymbolNavigationRequest request, string project, CancellationToken ct)
    {
        await git.RequireCleanCheckoutAsync(request.Root, request.Commit, ct);
        var tracked = (await git.TrackedPathsAsync(request.Root, request.Commit, ct)).ToHashSet(StringComparer.Ordinal);
        if (!(HasExtension(project, ".csproj") || HasExtension(project, ".sln") || HasExtension(project, ".slnx")) ||
            !HasExtension(request.Path, ".cs") || !tracked.Contains(project) || !tracked.Contains(request.Path))
            throw new StackerException("The selected project and file must belong to the checked-out commit.");

        using var workspace = MSBuildWorkspace.Create();
        var entry = Path.Combine(request.Root, project);
        try
        {
            if (HasExtension(entry, ".csproj")) await workspace.OpenProjectAsync(entry, cancellationToken: ct);
            else await workspace.OpenSolutionAsync(entry, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StackerException($"Cannot load {project}. Check the project's .NET SDK and restore its packages. {ex.Message}");
        }

        var failure = workspace.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure);
        if (failure is not null)
            throw new StackerException($"Cannot load {project}. Check the project's .NET SDK and restore its packages. {failure.Message}");

        var solution = workspace.CurrentSolution;
        var file = Path.Combine(request.Root, request.Path);
        var documents = solution.GetDocumentIdsWithFilePath(file).Select(solution.GetDocument)
            .OfType<Document>().Where(document => document.Project.Language == LanguageNames.CSharp).ToArray();
        if (documents.Length == 0) throw new StackerException($"{request.Path} is not included in {project}.");
        var source = await git.ReadBlobAsync(request.Root, request.Commit, request.Path, ct)
            ?? throw new StackerException("The source file is unavailable at this commit.");

        var locations = new HashSet<SymbolLocation>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var text = await document.GetTextAsync(ct);
            if (NormalizeNewlines(text.ToString()) != NormalizeNewlines(source.Text.TrimStart('\uFEFF')))
                throw new StackerException("The project source differs from the file shown in the diff.");
            if (request.Line < 1 || request.Line > text.Lines.Count) continue;
            var line = text.Lines[request.Line - 1];
            if (request.Column < 0 || request.Column >= line.Span.Length) continue;
            var position = line.Start + request.Column;
            var syntax = await document.GetSyntaxRootAsync(ct);
            if (syntax?.FindToken(position).Span.Contains(position) != true) continue;
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, ct);
            if (symbol is null) continue;
            names.Add(symbol.ToDisplayString());

            IEnumerable<Location> matches;
            if (request.Kind == SymbolNavigationKind.Definition)
            {
                var definition = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, ct) ?? symbol;
                matches = definition.Locations;
            }
            else
            {
                var references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
                matches = references.SelectMany(result => result.Locations.Select(reference => reference.Location));
            }

            foreach (var location in matches)
            {
                if (!location.IsInSource || location.SourceTree is not { FilePath.Length: > 0 } tree) continue;
                var path = Path.GetRelativePath(request.Root, tree.FilePath).Replace(Path.DirectorySeparatorChar, '/');
                if (!tracked.Contains(path)) continue;
                var start = location.GetLineSpan().StartLinePosition;
                locations.Add(new(path, start.Line + 1, start.Character));
            }
        }

        if (names.Count == 0) throw new StackerException("Place the cursor on a resolved C# symbol.");
        if (names.Count > 1) throw new StackerException("This position resolves to different symbols across project targets.");
        await git.RequireCleanCheckoutAsync(request.Root, request.Commit, ct);
        return new(names.Single(), locations.OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line).ThenBy(location => location.Column).Take(MaxResults).ToArray(),
            locations.Count > MaxResults);
    }

    private static bool HasExtension(string path, string extension) =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
