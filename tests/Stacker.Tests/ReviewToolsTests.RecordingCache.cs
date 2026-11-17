using Avalonia;
// Purpose: UI-oriented review tool scenarios for line ranges, file comments, and comment navigation.
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

namespace Stacker.Tests;

public sealed partial class ReviewToolsTests
{
    private sealed class RecordingCache(string root) : IPullRequestObjects
    {
        public List<long[]> Requests { get; } = [];
        public Task<Result<RepositoryObjectStorage>> InspectAsync(RepositoryIdentity repository, CancellationToken ct = default) => Task.FromResult(Result<RepositoryObjectStorage>.Ok(new(repository.CommonDirectory, 0)));
        public Task<Result<Unit>> EnsureAsync(RepositoryIdentity repository, GitHubRepositoryContext context, IReadOnlyList<PullRequest> prs, CancellationToken ct = default)
        {
            Assert.True(Directory.Exists(root));
            Requests.Add(prs.Select(pr => pr.Number).ToArray());
            return Task.FromResult(Result.Ok);
        }
        public Task<Result<ObjectSnapshotLease>> AcquireAsync(RepositoryIdentity repository, CancellationToken ct = default) => Task.FromResult(Result<ObjectSnapshotLease>.Ok(new(repository.Root, () => { })));
        public Task<Result<Unit>> ClearAsync(RepositoryIdentity repository, GitHubRepositoryContext context, CancellationToken ct = default) => Task.FromResult(Result.Ok);
    }
}
