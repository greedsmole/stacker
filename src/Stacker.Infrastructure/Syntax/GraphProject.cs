using Microsoft.CodeAnalysis;

namespace Stacker.Infrastructure;

internal sealed record GraphProject(Project Project, string Path, Compilation Compilation, IReadOnlySet<ProjectId> Reachable);
