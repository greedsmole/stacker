using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class DesktopTests
{
    private sealed class DelayedReader : IGitRepositoryReader
    {
        public TaskCompletionSource<FileDiff> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default) => file.NewPath == "a" ? Pending.Task : Task.FromResult(new FileDiff(file, "new", [], [new(DiffLineKind.Added, null, 1, "new")]));
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default) => throw new NotSupportedException();

        Task<Result<RepositorySnapshot>> IGitRepositoryReader.OpenAsync(string path, CancellationToken ct) => Boundary.CaptureAsync(() => OpenAsync(path, ct), ProblemCodes.GitFailed);

        Task<Result<IReadOnlyList<string>>> IGitRepositoryReader.MergeBasesAsync(string root, string left, string right, CancellationToken ct) => Boundary.CaptureAsync(() => MergeBasesAsync(root, left, right, ct), ProblemCodes.GitFailed);

        Task<Result<(int Ahead, int Behind)>> IGitRepositoryReader.CountsAsync(string root, string parent, string head, CancellationToken ct) => Boundary.CaptureAsync(() => CountsAsync(root, parent, head, ct), ProblemCodes.GitFailed);

        Task<Result<IReadOnlyList<FileChange>>> IGitRepositoryReader.FilesAsync(string root, string @base, string head, CancellationToken ct) => Boundary.CaptureAsync(() => FilesAsync(root, @base, head, ct), ProblemCodes.GitFailed);

        Task<Result<FileDiff>> IGitRepositoryReader.PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct) => Boundary.CaptureAsync(() => PatchAsync(root, @base, head, file, ct), ProblemCodes.GitFailed);
    }
}
