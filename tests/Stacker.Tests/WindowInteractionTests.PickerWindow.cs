using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

namespace Stacker.Tests;

public sealed partial class WindowInteractionTests
{
    /// <summary>Replaces only the native folder picker while retaining real window behavior.</summary>
    private sealed class PickerWindow : MainWindow
    {
        public TaskCompletionSource<string?> Selection { get; set; } = new();
        public int PickerCalls
        {
            get; private set;
        }
        public bool PopupWasOpenAtPicker
        {
            get; private set;
        }

        protected override Task<string?> PickRepositoryFolderAsync()
        {
            PickerCalls++;
            PopupWasOpenAtPicker = this.FindControl<Button>("RepositoryButton")!.Flyout!.IsOpen;
            return Selection.Task;
        }
    }
}
