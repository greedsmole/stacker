using Stacker.Core.Framework;

namespace Stacker.Core;

public enum SymbolNavigationKind { Definition, References }

/// <summary>A source coordinate on one exact side of a diff. Lines are one-based and columns are UTF-16 offsets.</summary>
public sealed record SymbolNavigationRequest(string Root, string Commit, string Path, int Line, int Column,
    SymbolNavigationKind Kind);

public sealed record SymbolLocation(string Path, int Line, int Column);
public sealed record SymbolNavigationResult(string Symbol, IReadOnlyList<SymbolLocation> Locations, bool Truncated);

public interface ISymbolNavigation
{
    Task<Result<IReadOnlyList<string>>> ProjectsAsync(SymbolNavigationRequest request, CancellationToken ct);
    Task<Result<SymbolNavigationResult>> FindAsync(SymbolNavigationRequest request, string project, CancellationToken ct);
}
