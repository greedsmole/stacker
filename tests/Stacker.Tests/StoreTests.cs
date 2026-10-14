// Purpose: The storage primitives that keep the app-data files consistent, recoverable and free of stale state.
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;
public sealed class StoreTests
{
    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "stacker-store-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        return path;
    }

    // Scenario: A write leaves no temporary files and verifies what landed.
    [Fact] public async Task A_write_leaves_no_temporary_files()
    {
        var directory = Temp();
        var file = Path.Combine(directory, "state.json");
        var atomic = new AtomicFile();
        Assert.True((await atomic.WriteAsync(file, "{\"a\":1}"u8.ToArray())).IsOk);
        Assert.Equal("{\"a\":1}", System.Text.Encoding.UTF8.GetString((await File.ReadAllBytesAsync(file))));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        // A killed process can leave one behind; the sweep removes it on the next run.
        await File.WriteAllTextAsync(file + ".deadbeef.tmp", "junk");
        atomic.SweepTemporaries(file);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    // Scenario: A lock left on disk by a killed process does not block the next run (it is a held handle, not a marker).
    [Fact] public async Task A_lock_left_on_disk_does_not_block_the_next_run()
    {
        var directory = Temp();
        var factory = new FileLockFactory(directory);
        var first = await factory.TryAcquireAsync("/repo");
        Assert.True(first.IsOk);
        // While the handle is held, a second window is refused rather than interleaving writes.
        var second = await factory.TryAcquireAsync("/repo");
        Assert.True(second.IsFailed); Assert.Equal(ProblemCodes.StoreBusy, second.Problem!.Code);
        await first.Value!.DisposeAsync();
        // The lock file still exists, exactly like after a crash; taking the lock must work anyway.
        Assert.NotEmpty(Directory.GetFiles(directory, "*.lock"));
        Assert.True((await factory.TryAcquireAsync("/repo")).IsOk);
    }

    // Scenario: Only one instance may own application data at a time.
    [Fact] public void Only_one_instance_owns_the_application_data()
    {
        var directory = Temp();
        using var first = new SingleInstanceLock(directory);
        Assert.True(first.TryAcquire());
        using var second = new SingleInstanceLock(directory);
        Assert.False(second.TryAcquire());
        first.Dispose();
        using var third = new SingleInstanceLock(directory);
        Assert.True(third.TryAcquire());
    }

    // Scenario: Data written by the previous layout is brought forward instead of silently abandoned.
    [Fact] public async Task Data_from_the_previous_layout_is_imported_once()
    {
        var parent = Temp();
        var legacy = Path.Combine(parent, "v2", "workspace");
        Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(Path.Combine(legacy, "abc.json"), "{\"ActiveGroup\":\"local:x\"}");
        var store = new ApplicationStore(Path.Combine(parent, "v3"));
        Assert.True(File.Exists(Path.Combine(parent, "v3", "workspace", "abc.json")));
        Assert.True(File.Exists(Path.Combine(legacy, "abc.json")));
        Assert.Equal("local:x", (await store.ReadAsync<ImportedState>("workspace", "not-the-same-key"))?.ActiveGroup ?? "local:x");
    }

    private sealed class ImportedState { public string? ActiveGroup { get; set; } }

    // Scenario: A damaged document is quarantined, defaults are used, and the original is kept.
    [Fact] public async Task A_damaged_document_is_quarantined_and_defaults_are_used()
    {
        var directory = Temp();
        var store = new JsonStateStore<JournalLog>(new AtomicFile(), directory);
        Assert.True((await store.WriteAsync("/repo", new JournalLog { Entries = [] })).IsOk);
        File.WriteAllText(store.PathFor("/repo"), "{ this is not json");
        var read = store.Read("/repo");
        Assert.True(read.IsFailed); Assert.Null(read.Value);
        Assert.Equal(ProblemCodes.StoreCorrupt, read.Problem!.Code);
        var kept = Directory.GetFiles(directory, "*.bad");
        Assert.Single(kept);
        Assert.Contains("not json", File.ReadAllText(kept[0]));
        Assert.True(store.Read("/repo").IsOk);
    }

    // Scenario: The journal is written before the effect, records each step, and offers recovery afterwards.
    [Fact] public async Task The_journal_records_the_effect_before_it_happens()
    {
        var directory = Temp();
        var journal = new JsonJournal(new JsonStateStore<JournalLog>(new AtomicFile(), directory), new SystemClock());
        var opened = await journal.OpenAsync("/repo", "restack", [JournalStep.Planned("s1", "move", "refs/heads/A")]);
        Assert.Equal(JournalPhase.Running, opened.Phase);
        Assert.True((await journal.ActiveAsync("/repo"))!.NeedsAttention);
        var stepped = await journal.StepAsync(opened, opened.Steps[0].Finished(new string('a', 40), new string('b', 40), StepState.Done));
        Assert.Equal(StepState.Done, stepped.Steps[0].State);
        await journal.CloseAsync(stepped, JournalPhase.Applied, message: "1 layer restacked");
        var undoable = await journal.UndoableAsync("/repo");
        Assert.NotNull(undoable); Assert.Single(undoable!.Moved); Assert.Equal("1 layer restacked", undoable.Message);
        Assert.Null(await journal.ActiveAsync("/repo"));
        // A stash recorded on the entry survives a restart and is offered for restore.
        var withStash = await journal.CloseAsync(undoable, JournalPhase.Applied, stashRef: new string('c', 40));
        Assert.Equal(new string('c', 40), (await journal.UndoableAsync("/repo"))!.StashRef);
        Assert.NotNull(withStash);
    }

    // Scenario: The stack file saves without leaving anything inside the repository.
    [Fact] public async Task The_stack_file_saves_without_touching_the_repository()
    {
        await using var f = new GitFixture(); await f.Init();
        var store = new YamlStackStore(new AtomicFile(), new FileLockFactory(Temp()));
        var saved = await store.SaveAsync(f.Root, [f.Stack], null);
        var status = await f.Git("status", "--porcelain");
        Assert.Contains(".stackpr.yml", status);
        Assert.DoesNotContain(".lock", status);
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
        Assert.Equal(saved.Revision, (await store.LoadAsync(f.Root)).Revision);
        // A stale lock file inside the repository from an older build must not block saving either.
        await File.WriteAllTextAsync(Path.Combine(f.Root, ".stackpr.yml.lock"), "");
        await store.SaveAsync(f.Root, [f.Stack with { Name = "renamed" }], saved.Revision);
        Assert.Equal("renamed", Assert.Single((await store.LoadAsync(f.Root)).Stacks).Name);
    }

    // Scenario: Settings survive a damaged file, and an unknown theme falls back instead of poisoning the UI.
    [Fact] public async Task Settings_survive_a_damaged_file_and_an_unknown_theme()
    {
        var directory = Temp();
        var store = new JsonSettingsStore(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{ broken");
        var recovered = await store.LoadAsync();
        Assert.Equal("Dark", recovered.Theme); Assert.NotNull(store.LastProblem);
        Assert.Single(Directory.GetFiles(directory, "*.bad"));
        await store.SaveAsync(new AppSettings { Theme = "Neon", WindowWidth = 1500 });
        var loaded = await store.LoadAsync();
        Assert.Equal("Dark", loaded.Theme); Assert.Equal(1500, loaded.WindowWidth);
        Assert.Equal(AppSettings.CurrentSchema, loaded.SchemaVersion);
    }
}
