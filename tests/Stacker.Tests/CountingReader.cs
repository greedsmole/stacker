using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class CountingReader(IGitRepositoryReader inner) : IGitRepositoryReader
{
    public int Opens
    {
        get; private set;
    }
    public bool FailOpen
    {
        get; set;
    }
    public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default)
    {
        Opens++;
        if (FailOpen)
            throw new StackerException("Test refresh failure");
        return Boundary.UnwrapAsync(inner.OpenAsync(path, ct));
    }
    public Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.MergeBasesAsync(root, left, right, ct));
    public Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.CountsAsync(root, parent, head, ct));
    public Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.FilesAsync(root, @base, head, ct));
    public Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.PatchAsync(root, @base, head, file, ct));

    Task<Result<RepositorySnapshot>> IGitRepositoryReader.OpenAsync(string path, CancellationToken ct) => Boundary.CaptureAsync(() => OpenAsync(path, ct), ProblemCodes.GitFailed);

    Task<Result<IReadOnlyList<string>>> IGitRepositoryReader.MergeBasesAsync(string root, string left, string right, CancellationToken ct) => Boundary.CaptureAsync(() => MergeBasesAsync(root, left, right, ct), ProblemCodes.GitFailed);

    Task<Result<(int Ahead, int Behind)>> IGitRepositoryReader.CountsAsync(string root, string parent, string head, CancellationToken ct) => Boundary.CaptureAsync(() => CountsAsync(root, parent, head, ct), ProblemCodes.GitFailed);

    Task<Result<IReadOnlyList<FileChange>>> IGitRepositoryReader.FilesAsync(string root, string @base, string head, CancellationToken ct) => Boundary.CaptureAsync(() => FilesAsync(root, @base, head, ct), ProblemCodes.GitFailed);

    Task<Result<FileDiff>> IGitRepositoryReader.PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct) => Boundary.CaptureAsync(() => PatchAsync(root, @base, head, file, ct), ProblemCodes.GitFailed);
}
