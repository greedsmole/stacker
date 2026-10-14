// Purpose: Commits the working tree from inside Stacker, with the user's hooks and signing configuration.
using Avalonia.Controls;
using Avalonia.Interactivity;
namespace Stacker.Desktop;
public partial class CommitWindow : Window
{
    private readonly ISyncActions? _sync;
    public CommitWindow() { InitializeComponent(); }
    public CommitWindow(ISyncActions sync) : this()
    {
        _sync = sync;
        var state = sync.WorkingTree;
        BranchLabel.Text = state?.Branch is { } branch ? "On " + branch : "HEAD is detached";
        ChangeList.ItemsSource = (state?.Entries ?? []).Select(entry => entry.Display).ToArray();
        CommitButton.IsEnabled = state is { Entries.Count: > 0 };
        MessageInput.Focus();
    }
    private async void CommitClick(object? sender, RoutedEventArgs e)
    {
        if (_sync is null) return;
        var message = MessageInput.Text?.Trim() ?? "";
        if (message.Length == 0) { ErrorLabel.Text = "Enter a commit message."; return; }
        IsEnabled = false; ErrorLabel.Text = "";
        try
        {
            var (ok, result) = await _sync.CommitAsync(message, StageAllToggle.IsChecked == true);
            if (ok) Close(true); else ErrorLabel.Text = result;
        }
        catch (Exception ex) { ErrorLabel.Text = ex.Message; }
        finally { IsEnabled = true; }
    }
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
