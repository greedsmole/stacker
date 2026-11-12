using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    internal ISymbolNavigation? SymbolNavigation { get; }

    internal async Task<BlobContent?> ReadSymbolSourceAsync(SymbolNavigationRequest request, SymbolLocation target, CancellationToken ct) =>
        _runtime._git is IBlobReader reader ? (await reader.ReadBlobAsync(request.Root, request.Commit, target.Path, ct)).OrThrow() : null;
}
