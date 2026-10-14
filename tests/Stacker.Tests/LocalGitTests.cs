// Purpose: Real-Git checks for the local commands — editor resolution, working-tree state, commit, stash, switch and stack push.
using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class LocalGitTests
{
    private static GitBranchWriter Writer(GitFixture f) => f.Writer();

    private static async Task MoveBase(GitFixture f)
    {
        await f.Git("checkout", "main"); await f.Write("main2.txt", "moved\n"); await f.Commit("base moved");
    }

    /// <summary>Records launches so an editor can be checked without opening one.</summary>
    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<ProcessRequest> Started { get; } = [];
        public void Start(ProcessRequest request) => Started.Add(request);
    }

    // Scenario: Porcelain v1 output decides staged, untracked, renamed and conflicted entries.
    [Fact] public void Porcelain_entries_are_classified()
    {
        var entries = WorkingTreeParser.Parse(" M a.txt\0M  b.txt\0?? new.txt\0R  moved.txt\0old.txt\0UU both.txt\0 M ü file.cs\0");
        Assert.Equal(6, entries.Count);
        Assert.False(entries[0].IsStaged); Assert.False(entries[0].IsUntracked);
        Assert.True(entries[1].IsStaged);
        Assert.True(entries[2].IsUntracked);
        Assert.Equal("moved.txt", entries[3].Path);
        Assert.True(entries[4].IsConflicted);
        Assert.Equal("ü file.cs", entries[5].Path);
    }

    // Scenario: A user-authored command is split without a shell, quotes included.
    [Fact] public void Editor_commands_split_without_a_shell()
    {
        var plain = CommandLine.Split("code --wait");
        Assert.Equal("code", plain.Program); Assert.Equal(["--wait"], plain.Arguments);
        var quoted = CommandLine.Split("\"/Applications/My Editor/bin/edit\" -w");
        Assert.Equal("/Applications/My Editor/bin/edit", quoted.Program); Assert.Equal(["-w"], quoted.Arguments);
        Assert.Throws<StackerException>(() => CommandLine.Split("   "));
    }

    // Scenario: The editor is resolved from the setting, then git, then the environment.
    [Fact] public async Task The_editor_is_resolved_from_the_setting_then_git_then_the_environment()
    {
        var launcher = new FakeLauncher();
        var configured = new ScriptedRunner(_ => new(0, "code --wait\n", ""));
        var launcherWithSetting = new EditorLauncher(configured, new(), launcher);
        Assert.Equal("my editor -w", await launcherWithSetting.ResolveAsync(".", "my editor -w"));
        Assert.Empty(configured.Calls);
        Assert.Equal("code --wait", await launcherWithSetting.ResolveAsync(".", null));
        var unset = new ScriptedRunner(_ => new(1, "", ""));
        var previous = Environment.GetEnvironmentVariable("VISUAL");
        try
        {
            Environment.SetEnvironmentVariable("VISUAL", "subl -w");
            Assert.Equal("subl -w", await new EditorLauncher(unset, new(), launcher).ResolveAsync(".", null));
        }
        finally { Environment.SetEnvironmentVariable("VISUAL", previous); }
    }

    // Scenario: Opening files passes each path as its own argument, never a shell string.
    [Fact] public async Task Opening_files_keeps_each_path_a_separate_argument()
    {
        var launcher = new FakeLauncher();
        var editor = new EditorLauncher(new ScriptedRunner(_ => new(1, "", "")), new(), launcher);
        var command = await editor.OpenAsync(".", ["src/ü file.cs", "b.txt"], "my editor --wait");
        Assert.Equal("my editor --wait", command);
        var call = Assert.Single(launcher.Started);
        Assert.Equal("my", call.Executable);
        Assert.Equal(new[] { "editor", "--wait", "src/ü file.cs", "b.txt" }, call.Arguments);
    }

    // Scenario: Staging names exactly what it takes, and nothing else.
    [Fact] public async Task Staging_names_exactly_what_it_takes()
    {
        await using var f = new GitFixture(); await f.Init();
        var writer = Writer(f);
        await f.Write("a.txt", "a\n"); await f.Write("b.txt", "b\n");
        Assert.Equal(new[] { "a.txt" }, await writer.StageAsync(f.Root, ["a.txt"]));
        var state = await writer.StateAsync(f.Root);
        Assert.True(state.Entries.Single(entry => entry.Path == "a.txt").IsStaged);
        Assert.True(state.Entries.Single(entry => entry.Path == "b.txt").IsUntracked);
        Assert.False(state.HasTrackedChanges == false);
        Assert.Contains("nothing to stage", (await Assert.ThrowsAsync<StackerException>(() => writer.StageAsync(f.Root, []))).Message);
    }

    // Scenario: A commit needs a message and something staged, and the user's hooks run.
    [Fact] public async Task A_commit_needs_a_message_and_runs_the_users_hooks()
    {
        await using var f = new GitFixture(); await f.Init();
        var writer = Writer(f);
        Assert.Contains("commit message", (await Assert.ThrowsAsync<StackerException>(() => writer.CommitAsync(f.Root, "  ", true))).Message);
        Assert.Contains("Nothing is staged", (await Assert.ThrowsAsync<StackerException>(() => writer.CommitAsync(f.Root, "message", false))).Message);
        // The marker lives outside the checkout so the working tree stays clean.
        var marker = Path.Combine(f.Root + " hook", "ran.txt");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            var hook = Path.Combine(f.Root, ".git", "hooks", "pre-commit");
            Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
            await File.WriteAllTextAsync(hook, $"#!/bin/sh\necho ran > \"{marker}\"\n");
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await f.Write("new.txt", "x\n");
        await writer.CommitAsync(f.Root, "Add a file", stageAll: true);
        Assert.Equal("Add a file", await f.Git("log", "-1", "--format=%s"));
        Assert.Empty((await f.Git("status", "--porcelain")).Trim());
        if (!OperatingSystem.IsWindows()) Assert.True(File.Exists(marker), "the user's pre-commit hook must run");
    }

    // Scenario: Switching branches moves only when the tracked working tree is clean.
    [Fact] public async Task Switching_branches_refuses_a_dirty_tree()
    {
        await using var f = new GitFixture(); await f.Linear();
        var writer = Writer(f);
        await writer.SwitchAsync(f.Root, "refs/heads/A");
        Assert.Equal("A", await f.Git("branch", "--show-current"));
        await f.Write("A.txt", "edited\n");
        Assert.Contains("uncommitted", (await Assert.ThrowsAsync<StackerException>(() => writer.SwitchAsync(f.Root, "refs/heads/B"))).Message);
        await f.Git("checkout", "--", "A.txt");
        await writer.SwitchAsync(f.Root, "refs/heads/B");
        Assert.Equal("B", await f.Git("branch", "--show-current"));
        Assert.Contains("does not exist", (await Assert.ThrowsAsync<StackerException>(() => writer.SwitchAsync(f.Root, "refs/heads/missing"))).Message);
    }

    // Scenario: A git binary that is not named "git" is still recorded in the diagnostics log.
    [Fact] public async Task A_configured_git_binary_is_still_logged()
    {
        if (OperatingSystem.IsWindows()) return; // creating a symlink needs privileges there
        await using var f = new GitFixture(); await f.Init();
        var directory = Path.Combine(Path.GetTempPath(), "stacker-git-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var renamed = Path.Combine(directory, "git-custom");
        File.CreateSymbolicLink(renamed, new GitExecutable().Resolve());
        try
        {
            var log = new GitCommandLog { GitExecutablePath = renamed };
            // Whether the renamed binary runs is the platform's business; being *recorded* is the point.
            await new ProcessRunner(log).RunAsync(new(renamed, ["--version"], f.Root));
            var entry = Assert.Single(log.Snapshot());
            Assert.False(entry.IsRunning);
            Assert.NotEqual("git", Path.GetFileName(renamed));
        }
        finally { Directory.Delete(directory, true); }
    }

    // Scenario: Pushing the stack publishes every layer, forcing only where the remote is not an ancestor.
    [Fact] public async Task Pushing_the_stack_publishes_every_layer()
    {
        await using var f = new GitFixture(); await f.Linear(); await f.AddOrigin();
        await f.Git("push", "origin", "main", "A", "B", "C");
        var writer = Writer(f);
        // New work on the top layer leaves every parent relationship intact, so nothing needs a rebase.
        await f.Git("checkout", "C"); await f.Write("C2.txt", "c2\n"); await f.Commit("C again");
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.False(plan.CanExecute);
        Assert.Empty(plan.PushSteps);
        Assert.Equal(3, plan.StackPushSteps.Count);
        Assert.Equal(new[] { "A", "B", "C" }, await writer.PushStackAsync(plan));
        foreach (var branch in new[] { "A", "B", "C" }) Assert.Equal(await f.Git("rev-parse", branch), await f.RemoteSha("refs/heads/" + branch));
        // Rewriting a published layer makes the remote tip a non-ancestor, so the same action must force.
        await f.Write("C.txt", "amended\n"); await f.Git("add", "-A"); await f.Git("commit", "--amend", "-m", "C amended");
        var rewritten = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.Equal(new[] { "A", "B", "C" }, await writer.PushStackAsync(rewritten));
        Assert.Equal(await f.Git("rev-parse", "C"), await f.RemoteSha("refs/heads/C"));
    }

    // Scenario: Merge commits inside a layer block a restack until the user asks to preserve them.
    [Fact] public async Task Merge_commits_are_preserved_when_the_user_asks()
    {
        await using var f = new GitFixture(); await f.Init();
        await f.Git("checkout", "-b", "side"); await f.Write("side.txt", "s\n"); await f.Commit("side");
        await f.Git("checkout", "main"); await f.Git("checkout", "-b", "A"); await f.Write("A.txt", "a\n"); await f.Commit("A");
        await f.Git("merge", "--no-ff", "-m", "merge side", "side");
        var stack = new StackDefinition("t", "T", "refs/heads/main", ["refs/heads/A"]);
        await MoveBase(f);
        var writer = Writer(f);
        var blocked = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), stack);
        Assert.False(blocked.CanExecute);
        Assert.Contains(blocked.Blockers, blocker => blocker.Contains("merge commits"));
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), stack, preserveMerges: true);
        Assert.True(plan.CanExecute, string.Join("; ", plan.Blockers));
        Assert.True((await writer.RestackAsync(plan)).Applied);
        Assert.True(int.Parse(await f.Git("rev-list", "--merges", "--count", "main..A")) > 0);
        Assert.Equal(0, await f.ExitCode("merge-base", "--is-ancestor", "main", "A"));
    }
}
