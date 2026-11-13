using Stacker.Core.Framework;

namespace Stacker.Core;

public interface ISymbolNavigation
{
    Task<Result<IReadOnlyList<string>>> ProjectsAsync(SymbolNavigationRequest request, CancellationToken ct);
    Task<Result<SymbolNavigationResult>> FindAsync(SymbolNavigationRequest request, string project, CancellationToken ct);
}
