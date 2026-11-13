using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class StackSyncUiTests
{
    /// <summary>An editor that records what it was asked to open, and that git can actually run for a commit message.</summary>
    private sealed class FakeEditor : IEditorLauncher
    {
        private readonly string _command;
        public FakeEditor(string command) => _command = command;
        public List<IReadOnlyList<string>> Opened { get; } = [];
        public Task<string?> ResolveAsync(string root, string? overrideCommand, CancellationToken ct = default) => Task.FromResult<string?>(_command);
        public Task<string?> OpenAsync(string root, IReadOnlyList<string> paths, string? overrideCommand, CancellationToken ct = default)
        {
            Opened.Add(paths);
            return Task.FromResult<string?>(_command);
        }

        Task<Result<string?>> IEditorLauncher.ResolveAsync(string root, string? overrideCommand, CancellationToken ct) => Boundary.CaptureAsync(() => ResolveAsync(root, overrideCommand, ct), ProblemCodes.GitFailed);

        Task<Result<string?>> IEditorLauncher.OpenAsync(string root, IReadOnlyList<string> paths, string? overrideCommand, CancellationToken ct) => Boundary.CaptureAsync(() => OpenAsync(root, paths, overrideCommand, ct), ProblemCodes.GitFailed);
    }
}
