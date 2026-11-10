using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;

namespace Stacker.Tests;

public sealed class StateMigrationTests
{
    private sealed class RecordingState : IApplicationState
    {
        private readonly MemoryApplicationState _inner = new();
        public List<(string Kind, string Identity)> Writes { get; } = [];
        public string Root => "test";
        public string? LastProblem => null;
        public bool FailStackWrite { get; set; }
        public Task<Result<T?>> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class => _inner.ReadAsync<T>(kind, identity, ct);
        public Task<Result<Unit>> WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class
        {
            Writes.Add((kind, identity));
            return FailStackWrite && kind == "workspace-stacks"
                ? Task.FromResult(Result<Unit>.Fail(ProblemCodes.StoreWrite, "disk full"))
                : _inner.WriteAsync(kind, identity, value, ct);
        }
    }

    [Fact]
    public async Task Legacy_workspace_import_preserves_views_and_only_rewrites_the_changed_stack()
    {
        var store = new RecordingState();
        var legacy = new WorkspaceState
        {
            ActiveGroup = "A",
            Stacks = new()
            {
                ["A"] = new() { Branches = ["refs/heads/A"], Comparisons = new() { ["Layer:A"] = [new("A", "one.cs", "one", 0, 123, true)] } },
                ["B"] = new() { Expanded = false }
            }
        };
        (await store.WriteAsync("workspace", "repo", legacy)).OrThrow();
        var persistence = new WorkspacePersistence(store);
        var state = (await persistence.LoadAsync("repo")).OrThrow();
        (await persistence.SaveAsync("repo", persistence.Snapshot("repo", state))).OrThrow();
        var index = (await store.ReadAsync<WorkspaceState>("workspace", "repo")).OrThrow()!;
        Assert.Equal(2, index.SchemaVersion); Assert.Empty(index.Stacks); Assert.Equal(2, index.StackKeys.Length);
        var restarted = new WorkspacePersistence(store);
        state = (await restarted.LoadAsync("repo")).OrThrow();
        Assert.False(state.Stacks["B"].Expanded);
        Assert.Equal(123, state.Stacks["A"].Comparisons["Layer:A"][0].Y);
        store.Writes.Clear();
        state.Stacks["A"].Expanded = false; state.Stacks["A"].Revision++;
        var snapshot = restarted.Snapshot("repo", state);
        state.Stacks["A"].Expanded = true; state.Stacks["A"].Revision++;
        (await restarted.SaveAsync("repo", snapshot)).OrThrow();
        Assert.Equal(new[] { ("workspace-stacks", "repo|A"), ("workspace", "repo") }, store.Writes);
        var saved = (await new WorkspacePersistence(store).LoadAsync("repo")).OrThrow();
        Assert.False(saved.Stacks["A"].Expanded); // The queued snapshot cannot change underneath the save.
    }

    [Fact]
    public async Task A_failed_stack_write_keeps_the_previous_workspace_index_and_remains_retryable()
    {
        var store = new RecordingState(); var persistence = new WorkspacePersistence(store);
        var state = new WorkspaceState { Stacks = new() { ["A"] = new() } };
        (await persistence.SaveAsync("repo", state)).OrThrow();
        state.Stacks["B"] = new(); store.FailStackWrite = true;
        Assert.True((await persistence.SaveAsync("repo", state)).IsFailed);
        Assert.NotNull(persistence.LastProblem);
        Assert.Equal(new[] { "A" }, (await store.ReadAsync<WorkspaceState>("workspace", "repo")).OrThrow()!.StackKeys);
        store.FailStackWrite = false;
        (await persistence.SaveAsync("repo", state)).OrThrow();
        Assert.Null(persistence.LastProblem);
        Assert.Equal(2, (await new WorkspacePersistence(store).LoadAsync("repo")).OrThrow().Stacks.Count);
    }

    [Fact]
    public async Task Saved_stashes_can_be_restored_by_identity_in_either_order_after_restart()
    {
        await using var f = new GitFixture(); await f.Linear();
        var root = await f.ResolvedRoot(); IGitBranchWriter writer = f.Writer();
        await f.Write("base.txt", "first stash\n");
        var first = (await writer.StashAsync(root, "first")).OrThrow()!;
        await f.Write("A.txt", "second stash\n");
        var second = (await writer.StashAsync(root, "second")).OrThrow()!;
        writer = f.Writer();
        Assert.Equal(new[] { second, first }, (await writer.SavedStashesAsync(root)).OrThrow().Select(item => item.Sha));
        (await writer.RestoreRecordedStashAsync(root, first)).OrThrow();
        Assert.Equal("first stash", (await File.ReadAllTextAsync(Path.Combine(root, "base.txt"))).Trim());
        Assert.Equal(second, Assert.Single((await writer.SavedStashesAsync(root)).OrThrow()).Sha);
        await f.Commit("keep first work");
        (await writer.RestoreRecordedStashAsync(root, second)).OrThrow();
        Assert.Equal("second stash", (await File.ReadAllTextAsync(Path.Combine(root, "A.txt"))).Trim());
        Assert.Empty((await writer.SavedStashesAsync(root)).OrThrow());
    }

    [Fact]
    public async Task Future_settings_are_not_overwritten_by_shutdown_defaults()
    {
        await using var f = new GitFixture(); var path = Path.Combine(f.Root, "settings.json");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":999,\"FutureValue\":\"preserve\"}");
        var before = await File.ReadAllTextAsync(path); ISettingsStore settings = new JsonSettingsStore(f.Root);
        Assert.True((await settings.LoadAsync()).IsFailed);
        Assert.True((await settings.SaveAsync(new())).IsFailed);
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_valid_json_journal_with_an_unsupported_schema_is_preserved_and_blocks_writes()
    {
        await using var f = new GitFixture(); var files = new AtomicFile();
        var store = new JsonStateStore<JournalLog>(files, f.Root + " journal", quarantineCorrupt: false);
        var entry = new JournalEntry(99, "future", f.Root, "restack", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, JournalPhase.Running, [], null, null, null);
        (await store.WriteAsync(f.Root, new JournalLog { Entries = [entry] })).OrThrow();
        var before = await File.ReadAllTextAsync(store.PathFor(f.Root));
        IJournal journal = new JsonJournal(store, new SystemClock());
        var result = await journal.OpenAsync(f.Root, "local.commit", []);
        Assert.Equal(ProblemCodes.StateInvalid, result.Problem!.Code);
        Assert.Equal(before, await File.ReadAllTextAsync(store.PathFor(f.Root)));
    }
}
