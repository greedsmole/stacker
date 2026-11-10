using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;

namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed class UiReviewTests
{
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task Secondary_screens_fit_and_keep_actions_available(string theme)
    {
        var root = Path.Combine(Path.GetTempPath(), "stacker-ui-" + Guid.NewGuid().ToString("N"));
        using var services = Composition.Build(root);
        var vm = services.GetRequiredService<MainViewModel>();
        vm.Settings.Theme = theme; vm.Settings.WindowWidth = 1060; vm.Settings.WindowHeight = 640;
        await vm.SaveSettingsAsync();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await Until(() => (bool)typeof(MainWindow).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
        Application.Current!.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        window.Width = 1060; window.Height = 640;
        try
        {
            Capture(window, theme, "empty");
            vm.IsBusy = true; Assert.False(vm.ShowEmptyState); Capture(window, theme, "loading"); vm.IsBusy = false;
            vm.OpenSearch("Files"); vm.SearchQuery = "Auth"; await vm.SearchAsync();
            Assert.Contains("Select a stack", vm.SearchStatus); Capture(window, theme, "search-no-comparison"); vm.CloseSearch();
            await vm.OpenDemoAsync();
            await vm.SelectGroupAsync(vm.LocalGroups[0]);
            vm.OpenSearch("Files"); vm.SearchQuery = "Auth"; await vm.SearchAsync();
            Assert.NotEmpty(vm.SearchResults); Capture(window, theme, "search-results");
            vm.SearchQuery = "no-match-unique"; await vm.SearchAsync(); Assert.Contains("No matches", vm.SearchStatus); Capture(window, theme, "search-empty");
            vm.SearchRegex = true; vm.SearchQuery = "["; await vm.SearchAsync(); Capture(window, theme, "search-invalid-regex"); vm.CloseSearch();
            vm.Error = "Cannot open the selected repository. The directory is unavailable. Reconnect its drive or use Open repository to choose its current location.\n" + string.Join("\n", Enumerable.Repeat("Git diagnostic details remain selectable and scrollable without hiding workspace actions.", 10));
            Capture(window, theme, "error"); Click(window, "Dismiss"); Assert.False(vm.HasError);
            await vm.ShowHistoryAsync(); Assert.NotNull(vm.ActiveHistory);
            await Until(() => !vm.IsBusy && !vm.ActiveHistory!.IsBusy && !vm.ActiveHistory.LoadLayerCommand.IsRunning);
            Capture(window, theme, "history");
            await vm.ActiveHistory!.ShowCurrentCommand.ExecuteAsync(null);
            Assert.NotEmpty(vm.ActiveHistory.DocumentSections); Capture(window, theme, "history-document");
            var historyPanel = window.GetLogicalDescendants().OfType<HistoryPanel>().Single();
            foreach (var expander in historyPanel.GetLogicalDescendants().OfType<Expander>()) expander.IsExpanded = true;
            Capture(window, theme, "history-tools");
            vm.ActiveHistory!.SelectedNode = null;
            Assert.False(vm.ActiveHistory.MarkReviewedCommand.CanExecute(null));
            Assert.False(vm.ActiveHistory.ShowCurrentCommand.CanExecute(null));
            Assert.False(vm.ActiveHistory.CompareVersionsCommand.CanExecute(null));
            Assert.Null(vm.ActiveHistory.SelectedFile); Assert.Null(vm.ActiveHistory.PreviousVersion);
            Assert.Contains("Select a layer", vm.ActiveHistory.Message);
            Capture(window, theme, "history-empty"); vm.CloseHistoryCommand.Execute(null);
            await CaptureDialog(window, "SettingsClick", theme, "settings");
            await CaptureDialog(window, "GitHubSettingsClick", theme, "github");
            await CaptureDialog(window, "HelpClick", theme, "help");
            await CaptureDialog(window, "RelocateRepositoryClick", theme, "relocate");
            var unsavedChoice = (Task<UnsavedCloseChoice>)typeof(MainWindow).GetMethod("AskUnsavedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { new[] { "The review draft could not be saved. The disk is full.", "Free disk space, then retry saving." } })!;
            await Until(() => window.OwnedWindows.Count > 0);
            var unsaved = window.OwnedWindows.Single(); Minimum(unsaved); Capture(unsaved, theme, "unsaved");
            Press(unsaved, Key.Escape); Assert.Equal(UnsavedCloseChoice.ContinueEditing, await unsavedChoice);
            var stack = vm.LocalGroups[0].Definition;
            var add = new AddLayerWindow(stack, (_, _, _) => Task.FromResult((false, "Not executed in visual review")));
            add.Show(window); Minimum(add); Click(add, "Create layer"); Capture(add, theme, "add-layer-validation"); Assert.Contains("Enter a branch", add.FindControl<TextBlock>("ErrorLabel")!.Text); add.Close();
            var edit = new StackEditorWindow(vm.Repository!, stack, _ => Task.FromResult(false)); edit.Show(window); Minimum(edit); Capture(edit, theme, "stack-editor"); edit.Close();
            await vm.RefreshWorkingTreeAsync();
            var commit = new CommitWindow(vm); commit.Show(window); Minimum(commit); Capture(commit, theme, "commit-clean"); Assert.False(commit.FindControl<Button>("CommitButton")!.IsEnabled); commit.Close();
            await File.AppendAllTextAsync(Path.Combine(vm.Repository!.Root, "Auth.cs"), "\n// visual review fixture\n");
            await vm.RefreshWorkingTreeAsync();
            var dirtyCommit = new CommitWindow(vm); dirtyCommit.Show(window); Minimum(dirtyCommit); await Task.Delay(30); Click(dirtyCommit, "Commit");
            Capture(dirtyCommit, theme, "commit-validation"); Assert.Equal("Enter a commit message.", dirtyCommit.FindControl<TextBlock>("ErrorLabel")!.Text); dirtyCommit.Close();
            var plan = await vm.BuildRestackPlanAsync(); Assert.NotNull(plan);
            var sync = new SyncPreviewWindow(plan!, vm, false); sync.Show(window); Minimum(sync); Capture(sync, theme, "sync-preview"); sync.Close();
        }
        finally { await vm.PrepareCloseAsync(); window.Close(); }
    }
    private static void Minimum(Window window)
    {
        window.Width = window.MinWidth > 0 ? window.MinWidth : Math.Min(window.Width, 600);
        window.Height = window.MinHeight > 0 ? window.MinHeight : Math.Min(window.Height, 560);
    }
    private static void Click(Control root, string label) => root.GetLogicalDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, "");
        if (window.IsVisible) window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, "");
    }
    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 300; i++) { if (predicate()) return; await Task.Delay(20); }
        Assert.True(predicate(), "UI state did not become available within six seconds.");
    }
    private static async Task CaptureDialog(MainWindow owner, string handler, string theme, string name)
    {
        typeof(MainWindow).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, new object?[] { null, new RoutedEventArgs() });
        await Until(() => owner.OwnedWindows.Count > 0);
        var dialog = owner.OwnedWindows.Single(); Minimum(dialog); Capture(dialog, theme, name);
        var body = dialog.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (body is not null) { body.Offset = new Vector(0, 10000); Capture(dialog, theme, name + "-bottom"); }
        Press(dialog, Key.Tab);
        Assert.NotNull(dialog.FocusManager?.GetFocusedElement());
        if (name == "settings") Capture(dialog, theme, name + "-focus");
        Press(dialog, Key.Escape); await Until(() => !dialog.IsVisible);
    }
    private static void Capture(Window window, string theme, string name)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.True(window.Bounds.Width > 0 && window.Bounds.Height > 0);
        var directory = Environment.GetEnvironmentVariable("STACKER_UI_CAPTURE_DIR");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame(); Assert.True(frame is not null, $"No rendered frame for {name}; visible={window.IsVisible}.");
        frame!.Save(Path.Combine(directory, theme + "-" + name + ".png"));
    }
}
