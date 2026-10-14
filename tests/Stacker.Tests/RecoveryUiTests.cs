// Purpose: The shutdown contract and the recovery banner: the window always closes, and an interrupted run is visible and actionable.
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class RecoveryUiTests
{
    private static MainViewModel CreateVm(GitFixture f, string? appRoot = null) => new(
        f.Reader, new YamlStackStore(), new JsonSettingsStore(appRoot ?? f.Root + " settings"), new GitExecutable(),
        new StackService(f.Reader), new DiffService(f.Reader), applicationStore: new ApplicationStore(appRoot ?? f.Root + " app"),
        branchWriter: f.Writer());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    // Scenario: A window must close even when nothing can be saved on the way out.
    [AvaloniaFact]
    public async Task A_window_closes_even_when_nothing_can_be_saved()
    {
        await using var f = new GitFixture(); await f.Linear();
        // A root that cannot be created on any platform: a directory below a file.
        var blocker = Path.Combine(Path.GetTempPath(), "stacker-blocker-" + Guid.NewGuid().ToString("N")[..8]);
        await File.WriteAllTextAsync(blocker, "not a directory");
        using var vm = CreateVm(f, Path.Combine(blocker, "stacker"));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.OpenRepositoryAsync(f.Root);
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
        Assert.False(window.IsVisible);
    }

    // Scenario: An interrupted run from an earlier session is shown in the banner and can be restored or dismissed.
    [AvaloniaFact]
    public async Task An_interrupted_run_is_offered_and_can_be_restored()
    {
        await using var f = new GitFixture(); await f.Linear();
        var journal = f.CreateJournal();
        using var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        Assert.False(vm.HasRecovery);
        var root = await f.ResolvedRoot();
        var oldA = await f.Git("rev-parse", "A");
        // What a killed process leaves behind: the entry open, one branch already moved, nothing closed.
        var entry = await journal.OpenAsync(root, "restack", [new JournalStep("layer:0", "rebase", "refs/heads/A", oldA, null, StepState.Planned)]);
        await f.Git("checkout", "A"); await f.Git("commit", "--amend", "-m", "A rewritten");
        await journal.StepAsync(entry, entry.Steps[0].Finished(oldA, await f.Git("rev-parse", "A"), StepState.Done));
        await vm.RefreshWorkingTreeAsync();
        Assert.True(vm.HasRecovery, vm.RecoverySummary);
        Assert.Contains("branch(es)", vm.RecoverySummary);
        Assert.Equal("Restore", vm.RecoveryAction);
        var (ok, message) = await vm.RestoreRecoveryAsync();
        Assert.True(ok, message);
        Assert.Equal(oldA, await f.Git("rev-parse", "A"));
        Assert.False(vm.HasRecovery);
    }

    // Scenario: Dismissing keeps the branches as they are and clears the banner.
    [AvaloniaFact]
    public async Task Dismissing_an_interrupted_run_keeps_the_branches()
    {
        await using var f = new GitFixture(); await f.Linear();
        var journal = f.CreateJournal();
        using var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        var root = await f.ResolvedRoot();
        var entry = await journal.OpenAsync(root, "restack", [new JournalStep("layer:0", "rebase", "refs/heads/A", await f.Git("rev-parse", "A"), null, StepState.Planned)]);
        await journal.StepAsync(entry, entry.Steps[0].Finished(await f.Git("rev-parse", "A"), await f.Git("rev-parse", "A"), StepState.Failed));
        await vm.RefreshWorkingTreeAsync();
        Assert.True(vm.HasRecovery);
        var (ok, message) = await vm.DismissRecoveryAsync();
        Assert.True(ok, message);
        Assert.False(vm.HasRecovery);
        Assert.False(vm.HasError, vm.Error);
    }

    // Scenario: The container and the shutdown path both dispose the view model, so it must tolerate it.
    [AvaloniaFact]
    public async Task Disposing_the_view_model_twice_is_harmless()
    {
        await using var f = new GitFixture(); await f.Linear();
        var vm = CreateVm(f);
        await vm.OpenRepositoryAsync(f.Root);
        vm.Dispose();
        vm.Dispose();
        var problems = await vm.ShutdownAsync();
        Assert.NotNull(problems);
    }
}
