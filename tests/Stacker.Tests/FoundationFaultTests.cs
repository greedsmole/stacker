using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class FoundationFaultTests
{
    [Fact]
    public async Task Broken_migration_rolls_back_and_preserves_the_original_and_backup()
    {
        var root = Directory.CreateTempSubdirectory("foundation-migrate-").FullName;
        try
        {
            using (var c = new SqliteConnection("Data Source=" + Path.Combine(root, "review-history.db") + ";Pooling=False"))
            {
                c.Open(); using var command = c.CreateCommand(); command.CommandText = "CREATE TABLE documents(kind TEXT,id TEXT,json TEXT,revision INTEGER);INSERT INTO documents VALUES('unknown','id','{}',1);PRAGMA user_version=1"; command.ExecuteNonQuery();
            }
            Assert.True((await new SqliteHistoryStore(root).GraphsAsync("s")).IsFailed);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "backups"), "*.db"));
            using var verify = new SqliteConnection("Data Source=" + Path.Combine(root, "review-history.db") + ";Pooling=False"); verify.Open(); using var query = verify.CreateCommand(); query.CommandText = "PRAGMA user_version"; Assert.Equal(1L, query.ExecuteScalar());
            query.CommandText = "SELECT COUNT(*) FROM documents"; Assert.Equal(1L, query.ExecuteScalar());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Damaged_backup_never_creates_a_partially_restored_workspace()
    {
        var root = Directory.CreateTempSubdirectory("foundation-archive-").FullName;
        try
        {
            var archive = Path.Combine(root, "broken.zip"); using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) await writer.WriteAsync(JsonSerializer.Serialize(new { Schema = 1, CreatedAt = DateTimeOffset.UtcNow, Files = new Dictionary<string, string> { { "review-history.db", "wrong-checksum" } } }));
                using var file = new StreamWriter(zip.CreateEntry("review-history.db").Open()); await file.WriteAsync("damaged");
            }
            var app = new ApplicationStore(Path.Combine(root, "app")); var backup = new WorkspaceBackupService(app, new(app.Root), new ProcessRunner()); var destination = Path.Combine(root, "restored");
            Assert.True((await backup.RestoreAsync(archive, destination)).IsFailed); Assert.False(Directory.Exists(destination)); Assert.Empty(Directory.GetDirectories(root, "*.restore-*"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Explicitly_removed_contents_keep_their_status_after_restart()
    {
        await using var f = new GitFixture(); await f.Init(); var root = Directory.CreateTempSubdirectory("foundation-objects-").FullName;
        try
        {
            var app = new ApplicationStore(root); var objects = new HistoryGit(f.Runner, new(), app);
            Assert.Equal(ContentAvailability.NeedsDownload, (await objects.AvailabilityAsync(f.Identity, "s")).OrThrow());
            var sha = await f.Git("rev-parse", "HEAD"); (await objects.CaptureAsync(f.Identity, "s", [sha])).OrThrow(); Assert.Equal(ContentAvailability.Available, (await objects.AvailabilityAsync(f.Identity, "s")).OrThrow());
            (await objects.ForgetContentsAsync(f.Identity, "s")).OrThrow(); objects = new(f.Runner, new(), app);
            Assert.Equal(ContentAvailability.Removed, (await objects.AvailabilityAsync(f.Identity, "s")).OrThrow());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Renaming_a_change_preserves_versions_and_checkpoints()
    {
        var root = Directory.CreateTempSubdirectory("foundation-ref-").FullName;
        try
        {
            var store = new SqliteHistoryStore(root); var node = (await store.ResolveNodeAsync("local:r", "refs/heads/old", "old")).OrThrow();
            var version = (await store.ObserveAsync(new("", node.Id, "base", "head", null, DateTimeOffset.UtcNow))).OrThrow();
            (await store.SetCheckpointAsync(new(node.Id, "file", version.Id, DateTimeOffset.UtcNow), false)).OrThrow();
            (await store.RebindLocalNodeAsync(node, "refs/heads/new")).OrThrow();
            Assert.Equal(node.Id, (await store.FindNodeAsync("local:r", "refs/heads/new")).OrThrow()!.Id); Assert.Single((await store.CheckpointsAsync(node.Id)).OrThrow()); Assert.Single((await store.VersionsAsync(node.Id)).OrThrow());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Last_query_subscriber_cancels_the_underlying_read()
    {
        using var coordinator = new QueryCoordinator(); using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = coordinator.RunAsync("one", async ct => { entered.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled.SetResult(); throw; } return 0; }, stop.Token);
        await entered.Task; stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
