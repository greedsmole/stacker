// Purpose: Headless scenarios for the sync window, the restack flow, and publishing a stack's pull requests.
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class StackSyncUiTests
{
    private static MainViewModel CreateVm(GitFixture f, IGitHubReader? github = null, IGitHubPullRequestWriter? pullRequests = null, IEditorLauncher? editor = null) => new(
        f.Reader, new YamlStackStore(), new JsonSettingsStore(f.Root + " settings"), new GitExecutable(),
        new StackService(f.Reader), new DiffService(f.Reader), github: github, pullRequestWriter: pullRequests,
        branchWriter: f.Writer(), editorLauncher: editor);

    /// <summary>An editor that records what it was asked to open, and that git can actually run for a commit message.</summary>
    private sealed class FakeEditor : IEditorLauncher
    {
        private readonly string _command;
        public FakeEditor(string command) => _command = command;
        public List<IReadOnlyList<string>> Opened { get; } = [];
        public Task<string?> ResolveAsync(string root, string? overrideCommand, CancellationToken ct = default) => Task.FromResult<string?>(_command);
        public Task<string?> OpenAsync(string root, IReadOnlyList<string> paths, string? overrideCommand, CancellationToken ct = default)
        { Opened.Add(paths); return Task.FromResult<string?>(_command); }
    }

    /// <summary>A real no-op program git can run as an editor on either platform, in a path without spaces (git runs GIT_EDITOR through a shell).</summary>
    private static string NoOpEditor()
    {
        if (OperatingSystem.IsWindows()) return "cmd /c exit 0";
        var directory = Path.Combine(Path.GetTempPath(), "stacker-editor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "noop-editor.sh");
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Leaves main moved under a layer that conflicts, with the stack definition committed.</summary>
    private static async Task Conflicting(GitFixture f)
    {
        await f.Init();
        await f.Git("checkout", "-b", "A"); await f.Write("conflict.txt", "A\n"); await f.Commit("A");
        await f.Git("checkout", "-b", "B"); await f.Write("B.txt", "B\n"); await f.Commit("B");
        await f.Git("checkout", "main"); await f.Write("conflict.txt", "main\n"); await f.Commit("main moved");
        await new YamlStackStore().SaveAsync(f.Root, [new("t", "T", "refs/heads/main", ["refs/heads/A", "refs/heads/B"])], null);
        await f.Commit("stack definition");
    }

    /// <summary>The view model is the sync surface the window talks to.</summary>
    private static ISyncActions Actions(MainViewModel vm) => vm;

    /// <summary>Leaves the checkout on main with a committed stack definition, optionally with the base already moved.</summary>
    private static async Task Prepared(GitFixture f, bool moveBase)
    {
        await f.Linear();
        await f.Git("checkout", "main");
        if (moveBase) await MoveBase(f);
        await new YamlStackStore().SaveAsync(f.Root, [f.Stack], null);
        await f.Commit("stack definition");
    }

    private static async Task MoveBase(GitFixture f)
    {
        await f.Git("checkout", "main"); await f.Write("main2.txt", "moved\n"); await f.Commit("base moved");
    }

    // Scenario: The preview lists every layer, its planned rewrite, and enables only the local action.
    [AvaloniaFact]
    public async Task Preview_lists_every_layer_and_waits_for_an_explicit_restack()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: true);
        using var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        var plan = await vm.BuildRestackPlanAsync();
        Assert.NotNull(plan); Assert.True(plan!.CanExecute, string.Join("; ", plan.Blockers));
        Assert.True(vm.CanRestackActiveStack);
        var window = new SyncPreviewWindow(plan, Actions(vm), false);
        window.Show();
        try
        {
            Assert.Equal(3, window.FindControl<ListBox>("StepList")!.ItemCount);
            Assert.False(window.FindControl<ItemsControl>("BlockerList")!.IsVisible);
            Assert.True(window.FindControl<Button>("RestackButton")!.IsEnabled);
            Assert.False(window.FindControl<Button>("PushButton")!.IsVisible);
            Assert.False(window.FindControl<ListBox>("PullRequestList")!.IsVisible);
            // Showing the plan must not have written anything.
            Assert.Equal(3, plan.RebaseCount);
            Assert.Equal(plan.Steps[0].BranchTip, await f.Git("rev-parse", "A"));
        }
        finally { window.Close(); }
    }

    // Scenario: A blocked plan explains itself and cannot be started.
    [AvaloniaFact]
    public async Task Preview_refuses_to_run_a_blocked_plan()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: true);
        using var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        await f.Write("base.txt", "edited\n");
        var plan = await vm.BuildRestackPlanAsync();
        Assert.NotNull(plan); Assert.False(plan!.CanExecute);
        var window = new SyncPreviewWindow(plan, Actions(vm), false);
        window.Show();
        try
        {
            Assert.True(window.FindControl<ItemsControl>("BlockerList")!.IsVisible);
            Assert.False(window.FindControl<Button>("RestackButton")!.IsEnabled);
        }
        finally { window.Close(); }
    }

    // Scenario: Restacking from the view model reloads the workspace and can be undone.
    [AvaloniaFact]
    public async Task View_model_restacks_reloads_and_undoes()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: false);
        using var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        var before = vm.Layers.Select(layer => layer.Snapshot.HeadSha).ToArray();
        Assert.False(vm.HasError, vm.Error);
        Assert.Equal(3, before.Length);
        await MoveBase(f); await vm.RefreshAsync();
        var plan = await vm.BuildRestackPlanAsync();
        Assert.NotNull(plan);
        var outcome = await vm.RestackAsync(plan);
        Assert.NotNull(outcome); Assert.True(outcome!.Applied, outcome.Message);
        Assert.False(vm.HasError, vm.Error);
        Assert.Equal(3, vm.Layers.Count);
        Assert.NotEqual(before[0], vm.Layers[0].Snapshot.HeadSha);
        Assert.True(vm.LocalGroups[0].UndoAvailable);
        var (ok, message) = await vm.UndoAsync();
        Assert.True(ok, message);
        Assert.Equal(before, vm.Layers.Select(layer => layer.Snapshot.HeadSha).ToArray());
        Assert.False(vm.LocalGroups[0].UndoAvailable);
    }

    // Scenario: Publishing creates the missing pull requests, retargets the moved ones, and leaves correct ones alone.
    [AvaloniaFact]
    public async Task Publishing_creates_missing_pull_requests_and_retargets_moved_ones()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: false);
        var github = new FakeGitHub([DiscoveryTests.Pr(7, "main", "A"), DiscoveryTests.Pr(8, "main", "B")]);
        using var vm = CreateVm(f, github, github);
        await vm.OpenRepositoryAsync(f.Root);
        var (plan, message) = await vm.CheckPullRequestsAsync("test");
        Assert.NotNull(plan); Assert.True(plan!.CanPublish, message);
        Assert.Equal(2, plan.PendingCount);
        Assert.Equal(PullRequestAction.Unchanged, plan.Steps[0].Action);
        Assert.False(vm.HasError, vm.Error);
        var (ok, published) = await vm.PublishPullRequestsAsync(plan);
        Assert.True(ok, published);
        Assert.False(vm.HasError, vm.Error);
        // Layer B's pull request pointed at main; layer C had none. A was already correct.
        Assert.Equal(new[] { "base #8 → A", "create C → B: C" }, github.Calls);
        // The same stack checked twice is idempotent: once the server agrees, nothing is pending.
        var (again, second) = await vm.CheckPullRequestsAsync("test");
        Assert.NotNull(again); Assert.Equal(0, again!.PendingCount); Assert.False(again.CanPublish);
        Assert.Contains("already has an open pull request", second);
    }

    // Scenario: Starting a layer creates the branch, checks it out, and appends it to the stack definition.
    [AvaloniaFact]
    public async Task Adding_a_layer_creates_the_branch_and_appends_it_to_the_stack()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: false);
        using var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        var (ok, message) = await vm.AddLayerAsync("test", "feature/top", "refs/heads/C");
        Assert.True(ok, message);
        Assert.False(vm.HasError, vm.Error);
        Assert.Equal("feature/top", await f.Git("branch", "--show-current"));
        Assert.Equal(await f.Git("rev-parse", "C"), await f.Git("rev-parse", "HEAD"));
        Assert.Equal(4, vm.Layers.Count);
        Assert.Contains("refs/heads/feature/top", vm.SelectedStack!.Branches);
        Assert.Equal("feature/top", vm.Layers[3].Snapshot.Branch.Replace("refs/heads/", ""));
        var saved = await new YamlStackStore().LoadAsync(f.Root);
        Assert.Contains("refs/heads/feature/top", Assert.Single(saved.Stacks).Branches);
    }

    // Scenario: The add-layer window creates the named branch from a real button press.
    [AvaloniaFact]
    public async Task The_add_layer_window_creates_the_named_branch()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: false);
        using var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        var window = new AddLayerWindow(Assert.Single(vm.Stacks), vm.AddLayerAsync);
        window.Show();
        try
        {
            window.FindControl<TextBox>("NameInput")!.Text = "feature/window-layer";
            window.FindControl<Button>("CreateButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !window.IsVisible);
            Assert.Equal(4, vm.Layers.Count);
            Assert.Equal("feature/window-layer", await f.Git("branch", "--show-current"));
        }
        finally { window.Close(); }
    }

    // Scenario: Pressing Check pull requests renders the plan and writes nothing until Publish is pressed.
    [AvaloniaFact]
    public async Task Pressing_check_pull_requests_renders_the_plan_and_waits_for_an_explicit_publish()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: false);
        var github = new FakeGitHub([DiscoveryTests.Pr(8, "main", "B")]);
        using var vm = CreateVm(f, github, github);
        await vm.OpenRepositoryAsync(f.Root);
        var plan = await vm.BuildRestackPlanAsync();
        Assert.NotNull(plan);
        var window = new SyncPreviewWindow(plan!, Actions(vm), false);
        window.Show();
        try
        {
            window.FindControl<Button>("CheckPrsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => window.FindControl<ListBox>("PullRequestList")!.IsVisible);
            Assert.Equal(3, window.FindControl<ListBox>("PullRequestList")!.ItemCount);
            Assert.Empty(github.Calls);
            var publish = window.FindControl<Button>("PublishButton")!;
            Assert.True(publish.IsEnabled);
            publish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !publish.IsVisible);
            Assert.Equal(new[] { "create A → main: A", "base #8 → A", "create C → B: C" }, github.Calls);
            Assert.False(vm.HasError, vm.Error);
        }
        finally { window.Close(); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    // Scenario: The main-window banner tracks a conflicted restack and clears once it is aborted.
    [AvaloniaFact]
    public async Task The_banner_tracks_a_conflicted_restack_and_clears_after_abort()
    {
        await using var f = new GitFixture(); await Conflicting(f);
        using var vm = CreateVm(f, editor: new FakeEditor("true"));
        await vm.OpenRepositoryAsync(f.Root);
        Assert.False(vm.HasOperation);
        var plan = await vm.BuildRestackPlanAsync();
        Assert.NotNull(plan); Assert.True(plan!.CanExecute, string.Join("; ", plan.Blockers));
        var outcome = await vm.RestackAsync(plan);
        Assert.NotNull(outcome); Assert.True(outcome!.Conflicted, outcome.Message);
        Assert.True(vm.HasOperation);
        Assert.Contains("rebase", vm.OperationSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conflicted file", vm.OperationSummary);
        Assert.True(vm.CanContinueOperation && vm.CanAbortOperation);
        var (ok, message) = await vm.AbortOperationAsync();
        Assert.True(ok, message);
        Assert.False(vm.HasOperation);
        Assert.False(vm.HasError, vm.Error);
    }

    // Scenario: The conflict panel opens the files, stages them and continues the rebase from real button presses.
    [AvaloniaFact]
    public async Task The_conflict_panel_resolves_and_continues_from_the_window()
    {
        await using var f = new GitFixture(); await Conflicting(f);
        var editor = new FakeEditor(NoOpEditor());
        using var vm = CreateVm(f, editor: editor);
        await vm.OpenRepositoryAsync(f.Root);
        var plan = await vm.BuildRestackPlanAsync();
        Assert.NotNull(plan);
        var outcome = await vm.RestackAsync(plan!);
        Assert.NotNull(outcome); Assert.True(outcome!.Conflicted, outcome.Message);
        var window = new SyncPreviewWindow(plan!, vm, false);
        window.Show();
        try
        {
            Assert.True(window.FindControl<Border>("ConflictPanel")!.IsVisible);
            Assert.Equal(1, window.FindControl<ListBox>("ConflictList")!.ItemCount);
            Assert.True(window.FindControl<Button>("ContinueButton")!.IsVisible);
            window.FindControl<Button>("ResolveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => editor.Opened.Count > 0);
            Assert.Contains("conflict.txt", Assert.Single(editor.Opened));
            // Resolve on disk, then stage and continue through the window.
            await f.Write("conflict.txt", "resolved\n");
            window.FindControl<Button>("StageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => vm.WorkingTree?.HasConflicts == false);
            window.FindControl<Button>("ContinueButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !vm.HasOperation);
            Assert.False(vm.HasConflicts);
            Assert.False(vm.HasError, vm.Error);
            Assert.Equal("resolved", (await f.Git("show", "A:conflict.txt")).Trim());
        }
        finally { window.Close(); }
    }

    // Scenario: A dirty tree is stashed by the app, restacked, and restored afterwards.
    [AvaloniaFact]
    public async Task Stashing_from_the_view_model_unblocks_a_dirty_tree()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: true);
        using var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        await f.Write("base.txt", "edited\n");
        await vm.RefreshWorkingTreeAsync();
        var plan = await vm.BuildRestackPlanAsync();
        Assert.NotNull(plan); Assert.False(plan!.CanExecute);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("uncommitted"));
        var (ok, message) = await vm.StashAndRestackAsync(plan);
        Assert.True(ok, message);
        Assert.True(vm.CanRestoreStash);
        var (restored, restoreMessage) = await vm.RestoreStashAsync();
        Assert.True(restored, restoreMessage);
        Assert.False(vm.CanRestoreStash);
        Assert.Equal("edited", File.ReadAllText(Path.Combine(f.Root, "base.txt")).Trim());
    }

    // Scenario: The commit window commits the staged working tree with a message.
    [AvaloniaFact]
    public async Task The_commit_window_commits_the_working_tree()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: false);
        using var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        await f.Write("new.txt", "x\n");
        await vm.RefreshWorkingTreeAsync();
        var window = new CommitWindow(vm);
        window.Show();
        try
        {
            window.FindControl<TextBox>("MessageInput")!.Text = "Add a file";
            window.FindControl<Button>("CommitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !window.IsVisible);
            Assert.Equal("Add a file", await f.Git("log", "-1", "--format=%s"));
            Assert.False(vm.HasError, vm.Error);
        }
        finally { window.Close(); }
    }

    // Scenario: A pull request opened between the check and the publish is never duplicated.
    [AvaloniaFact]
    public async Task Publishing_refuses_when_a_pull_request_appeared_after_the_check()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: false);
        var github = new FakeGitHub([DiscoveryTests.Pr(7, "main", "A"), DiscoveryTests.Pr(8, "main", "B")]);
        using var vm = CreateVm(f, github, github);
        await vm.OpenRepositoryAsync(f.Root);
        var (plan, _) = await vm.CheckPullRequestsAsync("test");
        Assert.NotNull(plan); Assert.True(plan!.CanPublish);
        // Someone opens the pull request for layer C while the preview is open.
        github.AddPullRequest(DiscoveryTests.Pr(9, "B", "C"));
        var (ok, message) = await vm.PublishPullRequestsAsync(plan);
        Assert.False(ok); Assert.Contains("changed since the check", message);
        Assert.Empty(github.Calls);
    }

    // Scenario: Without GitHub the check explains itself instead of writing.
    [AvaloniaFact]
    public async Task Checking_without_github_explains_itself()
    {
        await using var f = new GitFixture(); await Prepared(f, moveBase: false);
        using var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        var (plan, message) = await vm.CheckPullRequestsAsync("test");
        Assert.Null(plan); Assert.Contains("Connect GitHub", message);
    }

    /// <summary>In-memory server double: writes are reflected in later reads, so the flow can be checked end to end.</summary>
    private sealed class FakeGitHub : IGitHubReader, IGitHubPullRequestWriter
    {
        private readonly List<PullRequest> _pullRequests;
        public FakeGitHub(IReadOnlyList<PullRequest> pullRequests) => _pullRequests = [.. pullRequests];
        public GitHubRepositoryContext Context { get; } = new("github.com", 1, "team", "repo", "https://github.com/team/repo.git", "alice", "main");
        public List<string> Calls { get; } = [];
        /// <summary>Stands in for someone else opening a pull request while the preview is open.</summary>
        public void AddPullRequest(PullRequest pullRequest) => _pullRequests.Add(pullRequest);
        public Task<GitHubRepositoryContext> ConnectAsync(string root, string? repositoryOverride, CancellationToken ct = default) => Task.FromResult(Context);
        public Task<GitHubSnapshot> SnapshotAsync(GitHubRepositoryContext context, CancellationToken ct = default) => Task.FromResult(new GitHubSnapshot(context, _pullRequests.ToArray(), DateTimeOffset.UtcNow));
        public Task VerifyAccessAsync(GitHubRepositoryContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PullRequest> CreatePullRequestAsync(GitHubRepositoryContext context, string headBranch, string baseBranch, string title, string body, bool draft, CancellationToken ct = default)
        {
            Calls.Add($"create {headBranch} → {baseBranch}: {title}");
            var created = new PullRequest(100, title, context.RepositoryId, baseBranch, new('a', 40), context.RepositoryId, headBranch, new('b', 40), draft, "https://example.invalid/pr/100");
            _pullRequests.Add(created);
            return Task.FromResult(created);
        }
        public Task<PullRequest> UpdatePullRequestBaseAsync(GitHubRepositoryContext context, long number, string baseBranch, CancellationToken ct = default)
        {
            Calls.Add($"base #{number} → {baseBranch}");
            var index = _pullRequests.FindIndex(pr => pr.Number == number);
            var updated = _pullRequests[index] with { BaseRef = baseBranch };
            _pullRequests[index] = updated;
            return Task.FromResult(updated);
        }
        public Task<PullRequest> PullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReviewDiscussion> DiscussionAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateAnchorsAsync(GitHubRepositoryContext context, PullRequest pr, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ChangedFilePathsAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateFileAsync(GitHubRepositoryContext context, PullRequest pr, string path, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
