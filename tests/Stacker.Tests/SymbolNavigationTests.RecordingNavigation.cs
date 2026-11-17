using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

namespace Stacker.Tests;

public sealed partial class SymbolNavigationTests
{
    private sealed class RecordingNavigation : ISymbolNavigation
    {
        public List<SymbolNavigationRequest> Requests { get; } = [];
        public Task<Result<IReadOnlyList<string>>> ProjectsAsync(SymbolNavigationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Result<IReadOnlyList<string>>.Ok(["Code.csproj"]));
        }
        public Task<Result<SymbolNavigationResult>> FindAsync(SymbolNavigationRequest request, string project, CancellationToken ct) =>
            Task.FromResult(Result<SymbolNavigationResult>.Ok(new("Code", [], false)));
    }
}
