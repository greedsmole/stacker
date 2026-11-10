using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class FoundationConcurrencyTests
{
    [Fact]
    public async Task Autosave_drain_and_flush_do_not_depend_on_the_callers_dispatcher()
    {
        var saved = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new CoalescingWriter<int>(async value => { await Task.Delay(1); saved.SetResult(value); });
        var context = new UnpumpedContext(); var previous = SynchronizationContext.Current; Task queued; Task flushed;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            queued = writer.Queue(7); flushed = writer.FlushAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await Task.WhenAll(queued, flushed).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(7, await saved.Task); Assert.Equal(0, context.Posts);
    }
    [Fact]
    public async Task Shared_git_read_keeps_objects_alive_after_the_view_requests_removal()
    {
        var root = Directory.CreateTempSubdirectory("foundation-lease-").FullName;
        try
        {
            var app = new ApplicationStore(root); var objects = new ObjectStore(app); var path = Path.Combine(root, "objects", "repo"); Directory.CreateDirectory(path);
            var reader = new GatedReader(); using var cache = new CachingGitReader(reader, objects: objects, state: app);
            var file = new FileChange("a", "a", FileChangeKind.Modified, 1, 1, "100644", "100644");
            var reads = Enumerable.Range(0, 50).Select(_ => cache.PatchAsync(path, "base", "head", file)).ToArray(); await reader.Entered.Task;
            Assert.False((await objects.RemoveAsync(path)).OrThrow()); Assert.True(Directory.Exists(path));
            reader.Release.SetResult(); await Task.WhenAll(reads); (await objects.TrimAsync()).OrThrow(); Assert.False(Directory.Exists(path)); Assert.Equal(1, reader.Calls);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Read_budget_bounds_diff_work_even_when_many_files_are_requested()
    {
        using var scheduler = new ReadScheduler(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var peak = 0;
        var tasks = Enumerable.Range(0, 50).Select(_ => scheduler.RunAsync(ReadWork.Diff, async ct =>
        { var count = Interlocked.Increment(ref active); peak = Math.Max(peak, count); if (count == 2) entered.TrySetResult(); await release.Task.WaitAsync(ct); Interlocked.Decrement(ref active); return 0; })).ToArray();
        await entered.Task; Assert.Equal(2, active); release.SetResult(); await Task.WhenAll(tasks); Assert.Equal(2, peak);
    }
    [Fact]
    public async Task Moving_repository_folder_keeps_repository_and_worktree_identity()
    {
        await using var f = new GitFixture(); await f.Init(); var root = Directory.CreateTempSubdirectory("foundation-move-").FullName; var moved = f.Root + "-moved";
        try
        {
            var service = new ChangeIdentityService(new(root), f.Runner, new()); var original = (await service.RegisterAsync(await f.ResolvedRoot())).OrThrow();
            Directory.Move(f.Root, moved); var relocated = (await service.RelocateAsync(original.Repository, moved, original.Worktree)).OrThrow();
            Assert.Equal(original.Repository, relocated.Repository); Assert.Equal(original.Worktree, relocated.Worktree);
            Assert.Equal(relocated.Repository, (await service.RegisterAsync(moved)).OrThrow().Repository);
        }
        finally { Directory.Delete(root, true); if (Directory.Exists(moved)) Directory.Move(moved, f.Root); }
    }
    private sealed class GatedReader : IGitRepositoryReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<Result<FileDiff>> PatchAsync(string root, string basis, string head, FileChange file, CancellationToken ct = default)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Release.Task; return Result<FileDiff>.Ok(new(file, "", [], [])); }
        public Task<Result<RepositorySnapshot>> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<string>>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<(int Ahead, int Behind)>> CountsAsync(string root, string parent, string head, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<FileChange>>> FilesAsync(string root, string basis, string head, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class UnpumpedContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
    }
}
