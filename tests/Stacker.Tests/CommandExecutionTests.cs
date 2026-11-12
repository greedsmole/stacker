using Stacker.Core;
using Stacker.Core.Features.LocalGit;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;

namespace Stacker.Tests;

public sealed class CommandExecutionTests
{
    [Fact]
    public async Task Foreground_mutations_are_single_flight_and_report_their_result()
    {
        await using var f = new GitFixture();
        var journal = f.CreateJournal(); var bus = new CommandBus(new CommandRunner(journal));
        var release = new TaskCompletionSource<Result<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new List<CommandOutcome>(); bus.Reported += reports.Add;
        var first = new CommitCommand(CommitMessage.Parse("message").OrThrow(), (_, _) => { entered.SetResult(); return release.Task; });
        var running = bus.RunAsync(RepoRoot.From(f.Root), first);
        await entered.Task;
        var called = false;
        var second = new CommitCommand(CommitMessage.Parse("other").OrThrow(), (_, _) => { called = true; return Task.FromResult(Result<string>.Ok("sha")); });
        Assert.Equal(CommandStatus.Refused, (await bus.RunAsync(RepoRoot.From(f.Root), second)).Status);
        Assert.False(called);
        release.SetResult(Result<string>.Ok(new string('a', 40)));
        Assert.True((await running).Ok); Assert.Equal(2, reports.Count);
        Assert.Equal(StepState.Done, (journal.Recent(f.Root)).OrThrow()[0].Steps[0].State);
    }

    [Fact]
    public async Task Closing_an_old_entry_does_not_erase_finished_steps()
    {
        await using var f = new GitFixture(); var journal = f.CreateJournal();
        var original = (await journal.OpenAsync(f.Root, "test", [JournalStep.Planned("s", "write", "target")])).OrThrow();
        (await journal.StepAsync(original, original.Steps[0] with { State = StepState.Started })).OrThrow();
        (await journal.StepAsync(original, original.Steps[0].Finished("old", "new", StepState.Done))).OrThrow();
        (await journal.CloseAsync(original, JournalPhase.Applied)).OrThrow();
        Assert.Equal(StepState.Done, ((await journal.ByIdAsync(f.Root, original.Id)).OrThrow())!.Steps[0].State);
    }

    [Fact]
    public async Task The_journal_rejects_invalid_transitions_and_preserves_the_terminal_record()
    {
        await using var f = new GitFixture(); var journal = f.CreateJournal();
        var planned = JournalStep.Planned("s", "write", "target");
        var original = (await journal.OpenAsync(f.Root, "test", [planned])).OrThrow();
        var started = planned with { State = StepState.Started };
        var done = started.Finished("old", "new", StepState.Done);

        Assert.Equal(ProblemCodes.StateInvalid, (await journal.StepAsync(original, done)).Problem?.Code);
        Assert.Equal(ProblemCodes.StateInvalid, (await journal.StepAsync(original, started with { Id = "missing" })).Problem?.Code);
        (await journal.StepAsync(original, started)).OrThrow();
        (await journal.StepAsync(original, done)).OrThrow();
        var applied = (await journal.CloseAsync(original, JournalPhase.Applied, message: "confirmed")).OrThrow();

        Assert.Equal(ProblemCodes.StateInvalid, (await journal.StepAsync(original, started)).Problem?.Code);
        Assert.Equal(ProblemCodes.StateInvalid, (await journal.CloseAsync(original, JournalPhase.NeedsUser)).Problem?.Code);
        Assert.Equal(applied.UpdatedAt, (await journal.StepAsync(original, done)).OrThrow().UpdatedAt);
        var replay = (await journal.CloseAsync(original, JournalPhase.Applied, message: "stale message")).OrThrow();
        Assert.Equal(applied.UpdatedAt, replay.UpdatedAt);
        Assert.Equal("confirmed", replay.Message);
    }

    [Fact]
    public async Task A_pause_can_resume_admission_but_a_stop_is_permanent()
    {
        await using var f = new GitFixture(); var bus = new CommandBus(new CommandRunner(f.CreateJournal()));
        var root = RepoRoot.From(f.Root);
        var timeout = TimeSpan.FromSeconds(5);
        var calls = 0;
        CommitCommand Command() => new(CommitMessage.Parse("message").OrThrow(), (_, _) =>
        {
            calls++;
            return Task.FromResult(Result<string>.Ok(new string('a', 40)));
        });

        Assert.True(await bus.PauseAsync(timeout));
        Assert.Equal(CommandStatus.Refused, (await bus.RunAsync(root, Command())).Status);
        bus.Resume();
        Assert.True((await bus.RunAsync(root, Command())).Ok);
        Assert.True(await bus.StopAsync(timeout));
        Assert.True(await bus.PauseAsync(timeout));
        bus.Resume();
        Assert.Equal(CommandStatus.Refused, (await bus.RunAsync(root, Command())).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancellation_after_start_is_recorded_prevents_dispatch()
    {
        await using var f = new GitFixture();
        using var cancellation = new CancellationTokenSource();
        var store = new JsonStateStore<JournalLog>(new AtomicFile(), f.Root + " journal");
        IJournal journal = new JsonJournal(new CancelAfterStartStore(store, cancellation), new SystemClock());
        var calls = 0;
        var command = new CommitCommand(CommitMessage.Parse("message").OrThrow(), (_, _) =>
        {
            calls++;
            return Task.FromResult(Result<string>.Ok(new string('a', 40)));
        });

        var outcome = await new CommandRunner(journal).RunAsync(f.Root, command, ct: cancellation.Token);

        Assert.Equal(0, calls);
        Assert.Equal(CommandStatus.Cancelled, outcome.Status);
        Assert.Equal(EffectCertainty.NotStarted, outcome.Certainty);
        Assert.Equal(JournalPhase.Dismissed, Assert.Single(journal.Recent(f.Root).OrThrow()).Phase);
    }

    private sealed class CancelAfterStartStore(IStateStore<JournalLog> inner, CancellationTokenSource cancellation) : IStateStore<JournalLog>
    {
        public Result<JournalLog?> Read(string identity) => inner.Read(identity);
        public Result<string> Quarantine(string identity) => inner.Quarantine(identity);
        public async Task<Result<Unit>> WriteAsync(string identity, JournalLog value, CancellationToken ct = default)
        {
            var result = await inner.WriteAsync(identity, value, ct);
            if (result.IsOk && value.Entries.Any(entry => entry.Phase == JournalPhase.Running && entry.Steps.Any(step => step.State == StepState.Started)))
                cancellation.Cancel();
            return result;
        }
    }

    [Fact]
    public async Task Cancellation_after_an_acknowledged_effect_preserves_the_result()
    {
        await using var f = new GitFixture(); var journal = f.CreateJournal();
        using var cancellation = new CancellationTokenSource();
        var sha = new string('a', 40);
        var command = new CommitCommand(CommitMessage.Parse("message").OrThrow(), (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Result<string>.Ok(sha));
        });

        var outcome = await new CommandRunner(journal).RunAsync(f.Root, command, ct: cancellation.Token);

        Assert.Equal(CommandStatus.Applied, outcome.Status);
        var recorded = Assert.Single(journal.Recent(f.Root).OrThrow());
        Assert.Equal(JournalPhase.Applied, recorded.Phase);
        Assert.Equal(StepState.Done, recorded.Steps[0].State);
        Assert.Equal(sha, recorded.Steps[0].NewSha);
    }

    [Fact]
    public async Task Production_local_client_runs_real_commands_through_the_bus()
    {
        await using var f = new GitFixture(); await f.Init();
        var journal = f.CreateJournal(); var bus = new CommandBus(new CommandRunner(journal));
        var reports = new List<CommandOutcome>(); bus.Reported += reports.Add;
        var client = new LocalGitClient(f.Runner, new(), f.Reader, journal, new SystemClock(), bus);
        await f.Write("new.txt", "new\n");
        var sha = await client.CommitAsync(f.Root, "real command", true);
        Assert.Equal(await f.Git("rev-parse", "HEAD"), sha);
        Assert.Single(reports); Assert.True(reports[0].Ok);
        Assert.Equal("local.commit", (journal.Recent(f.Root)).OrThrow()[0].Kind);
        Assert.Equal(JournalPhase.Applied, (journal.Recent(f.Root)).OrThrow()[0].Phase);
    }

    [Fact]
    public async Task Production_remote_writer_preserves_a_rejected_send_as_a_failed_attempt()
    {
        await using var f = new GitFixture(); var journal = f.CreateJournal();
        var bus = new CommandBus(new CommandRunner(journal));
        var cli = new GitHubCli(new ScriptedRunner(_ => new(1, "{\"message\":\"Validation Failed\"}", "HTTP 422")), new(), new());
        var writer = new CommandGitHubWriter(cli, bus);
        await Assert.ThrowsAsync<StackerException>(() => writer.CommentAsync(FakeGitHub.Context, 42, "keep text"));
        var entry = (journal.Recent("github:" + FakeGitHub.Context.Identity)).OrThrow()[0];
        Assert.Equal("github.comment", entry.Kind); Assert.Equal(JournalPhase.Dismissed, entry.Phase);
        Assert.NotEqual(StepState.Done, entry.Steps[0].State);
    }

    [Fact]
    public async Task A_corrupt_critical_journal_keeps_blocking_until_it_is_resolved()
    {
        await using var f = new GitFixture();
        var path = f.Root + " journal";
        var store = new JsonStateStore<JournalLog>(new AtomicFile(), path, quarantineCorrupt: false);
        Directory.CreateDirectory(path); await File.WriteAllTextAsync(store.PathFor(f.Root), "{ broken");
        var journal = new JsonJournal(store, new SystemClock());
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<StackerException>(() => journal.OpenAsync(f.Root, "test", []));
        Assert.Equal("{ broken", await File.ReadAllTextAsync(store.PathFor(f.Root)));
    }

    [Fact]
    public async Task A_late_remote_change_is_detected_before_pushing_the_first_layer()
    {
        await using var f = new GitFixture(); await f.Linear(); await f.AddOrigin();
        await f.Git("push", "origin", "main", "A", "B", "C");
        await f.Git("checkout", "main"); await f.Write("moved.txt", "moved\n"); await f.Commit("advance");
        var writer = f.Writer(); var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.True((await writer.RestackAsync(plan)).Applied);
        var originalA = await f.RemoteSha("refs/heads/A");
        await f.MoveRemote("refs/heads/C", originalA);
        await Assert.ThrowsAsync<StackerException>(() => writer.PushStackAsync(plan));
        Assert.Equal(originalA, await f.RemoteSha("refs/heads/A"));
    }
}
