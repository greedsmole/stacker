using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

namespace Stacker.Tests;

/// <summary>Exercises real window events while replacing only the native folder picker.</summary>
public sealed class WindowInteractionTests
{
    [AvaloniaFact]
    public async Task Folder_picker_closes_repository_popup_and_ignores_duplicate_clicks()
    {
        await using var fixture = new GitFixture();
        using var vm = CreateVm(fixture);
        var window = new PickerWindow { DataContext = vm };
        window.Show();
        try
        {
            var repository = window.FindControl<Button>("RepositoryButton")!;
            repository.Flyout!.ShowAt(repository);
            Assert.True(repository.Flyout.IsOpen);

            ClickOpen(window);
            ClickOpen(window);
            await DrainUiAsync();

            Assert.Equal(1, window.PickerCalls);
            Assert.False(window.PopupWasOpenAtPicker);
            Assert.False(repository.Flyout.IsOpen);
            Assert.True(window.IsEnabled);
            Assert.Null(window.Owner);

            window.Selection.SetResult(null);
            await DrainUiAsync();
            Assert.Null(vm.Repository);
            Assert.False(vm.HasError, vm.Error);

            // Cancelling must release the guard so the next user action can show a new picker.
            window.Selection = new();
            ClickOpen(window);
            await DrainUiAsync();
            Assert.Equal(2, window.PickerCalls);
            window.Selection.SetResult(null);
            await DrainUiAsync();
        }
        finally { window.Close(); await DrainUiAsync(); }
    }

    [AvaloniaFact]
    public async Task Folder_selection_opens_repository_in_existing_main_window()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        using var vm = CreateVm(fixture);
        var window = new PickerWindow { DataContext = vm };
        window.Show();
        try
        {
            ClickOpen(window);
            await DrainUiAsync();
            window.Selection.SetResult(fixture.Root);
            await WaitUntilAsync(() => vm.Repository is not null && !vm.IsBusy);

            Assert.Equal(await fixture.Git("rev-parse", "--show-toplevel"), vm.Repository!.Root);
            Assert.Same(vm, window.DataContext);
            Assert.True(window.IsVisible);
            Assert.True(window.IsEnabled);
            Assert.Null(window.Owner);
            Assert.Empty(window.OwnedWindows);
            Assert.False(vm.HasError, vm.Error);
        }
        finally { window.Close(); await DrainUiAsync(); }
    }

    [AvaloniaFact]
    public async Task Recent_repository_selection_closes_popup_without_opening_native_picker()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        using var vm = CreateVm(fixture);
        var window = new PickerWindow { DataContext = vm };
        window.Show();
        try
        {
            var repository = window.FindControl<Button>("RepositoryButton")!;
            repository.Flyout!.ShowAt(repository);
            vm.RecentRepositories.Add(fixture.Root);
            window.FindControl<ComboBox>("RecentBox")!.SelectedItem = fixture.Root;
            await WaitUntilAsync(() => vm.Repository is not null && !vm.IsBusy);

            Assert.False(repository.Flyout.IsOpen);
            Assert.Equal(0, window.PickerCalls);
            Assert.True(window.IsEnabled);
            Assert.False(vm.HasError, vm.Error);
        }
        finally { window.Close(); await DrainUiAsync(); }
    }

    [AvaloniaFact]
    public async Task Picker_failure_leaves_window_usable_and_allows_retry()
    {
        await using var fixture = new GitFixture();
        using var vm = CreateVm(fixture);
        var window = new PickerWindow { DataContext = vm };
        window.Show();
        try
        {
            ClickOpen(window);
            await DrainUiAsync();
            window.Selection.SetException(new InvalidOperationException("Picker unavailable"));
            await WaitUntilAsync(() => vm.HasError);
            Assert.Equal("Picker unavailable", vm.Error);
            Assert.True(window.IsEnabled);

            window.Selection = new();
            ClickOpen(window);
            await DrainUiAsync();
            Assert.Equal(2, window.PickerCalls);
            window.Selection.SetResult(null);
            await DrainUiAsync();
        }
        finally { window.Close(); await DrainUiAsync(); }
    }

    [AvaloniaFact]
    public async Task Late_picker_result_after_window_close_does_not_open_repository()
    {
        await using var fixture = new GitFixture();
        using var vm = CreateVm(fixture);
        var window = new PickerWindow { DataContext = vm };
        window.Show();
        ClickOpen(window);
        await DrainUiAsync();
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
        window.Selection.SetResult(fixture.Root);
        await DrainUiAsync();
        Assert.Null(vm.Repository);
    }

    private static MainViewModel CreateVm(GitFixture fixture) => new(fixture.Reader, new YamlStackStore(),
        new JsonSettingsStore(Path.Combine(fixture.Root, "settings")), new(), new(fixture.Reader), new(fixture.Reader),
        applicationStore: new ApplicationStore(Path.Combine(fixture.Root, "app-data")));

    private static void ClickOpen(MainWindow window) => window.FindControl<Button>("OpenFolderButton")!
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task DrainUiAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class PickerWindow : MainWindow
    {
        public TaskCompletionSource<string?> Selection { get; set; } = new();
        public int PickerCalls { get; private set; }
        public bool PopupWasOpenAtPicker { get; private set; }

        protected override Task<string?> PickRepositoryFolderAsync()
        {
            PickerCalls++;
            PopupWasOpenAtPicker = this.FindControl<Button>("RepositoryButton")!.Flyout!.IsOpen;
            return Selection.Task;
        }
    }
}
