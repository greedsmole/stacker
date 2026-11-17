using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Stacker.Core;

namespace Stacker.Infrastructure;

internal static class RoslynGraphExtractor
{
    internal static async Task<CSharpGraph> ExtractAsync(MSBuildWorkspace workspace, string root, string commit,
        IReadOnlySet<string> tracked, CancellationToken ct)
    {
        var diagnostics = new ConcurrentQueue<string>(workspace.Diagnostics.Select(item => item.Message));
        using var subscription = workspace.RegisterWorkspaceFailedHandler(item => diagnostics.Enqueue(item.Diagnostic.Message));
        var solution = workspace.CurrentSolution;
        var paths = new RoslynTrackedPaths(root, tracked);
        var projects = new List<GraphProject>();
        foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            ct.ThrowIfCancellationRequested();
            var path = project.FilePath is { } file ? paths.Resolve(file) : null;
            if (path is null)
            {
                diagnostics.Enqueue($"Project is outside the checked-in analysis scope: {project.FilePath ?? project.Name}.");
                continue;
            }
            if (await project.GetCompilationAsync(ct) is not { } compilation)
            {
                diagnostics.Enqueue($"Cannot compile {path}.");
                continue;
            }
            foreach (var error in compilation.GetDiagnostics(ct).Where(item => item.Severity == DiagnosticSeverity.Error).Take(5))
                diagnostics.Enqueue($"{path}: {error.Id} {error.GetMessage()}");
            projects.Add(new(project, path, compilation, ReachableProjects(solution, project.Id)));
        }

        if (projects.Count == 0)
            diagnostics.Enqueue("The entry did not load any checked-in C# projects.");

        var assemblies = projects.ToLookup(project => project.Compilation.Assembly.Identity);
        var tables = projects.Select(project => new RoslynGraphSymbols(paths, project, assemblies)).ToArray();
        var types = tables.SelectMany(table => table.ReadTypes(ct))
            .GroupBy(type => type.Id, StringComparer.Ordinal)
            .Select(group => MergeType(group, ct)).ToImmutableArray();
        var typeIds = types.Select(type => type.Id).ToHashSet(StringComparer.Ordinal);
        var relationships = tables.SelectMany(table => table.ReadRelationships(typeIds, ct)).Distinct().ToImmutableArray();
        var coverage = projects.Select(project => project.Project.OutputFilePath is { } output
            ? $"{project.Path} · {RoslynWorkspace.RelativePath(root, output)}"
            : project.Path).Distinct().Order(StringComparer.Ordinal).ToImmutableArray();
        return new(commit, types, relationships, coverage, diagnostics.Concat(paths.Diagnostics).Distinct().ToImmutableArray());
    }

    private static CSharpType MergeType(IGrouping<string, CSharpType> group, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var type = group.First();
        return type with
        {
            DeclarationHash = MergeHashes(group.Select(item => item.DeclarationHash)),
            Locations = MergeLocations(group.SelectMany(item => item.Locations)),
            Members = group.SelectMany(item => item.Members).GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(MergeMember).ToImmutableArray()
        };
    }

    private static CSharpMember MergeMember(IGrouping<string, CSharpMember> group)
    {
        var member = group.First();
        return member with
        {
            Signature = string.Join(" / ", group.Select(item => item.Signature).Distinct().Order(StringComparer.Ordinal)),
            DeclarationHash = MergeHashes(group.Select(item => item.DeclarationHash)),
            Locations = MergeLocations(group.SelectMany(item => item.Locations))
        };
    }

    private static string MergeHashes(IEnumerable<string> hashes) => string.Join(",", hashes.Distinct().Order(StringComparer.Ordinal));

    private static ImmutableArray<SymbolLocation> MergeLocations(IEnumerable<SymbolLocation> locations) =>
        locations.Distinct().OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line).ThenBy(location => location.Column).ToImmutableArray();

    private static HashSet<ProjectId> ReachableProjects(Solution solution, ProjectId start)
    {
        var graph = solution.GetProjectDependencyGraph();
        return [start, .. graph.GetProjectsThatThisProjectTransitivelyDependsOn(start)];
    }
}
