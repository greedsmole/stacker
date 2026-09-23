using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Stacker.Core;
namespace Stacker.Desktop;
public partial class StackEditorWindow : Window
{
    private readonly ObservableCollection<string> _branches = [];
    private string _id = Guid.NewGuid().ToString("N");
    private readonly Func<StackDefinition, Task<bool>>? _save;
    public bool DeleteRequested { get; private set; }
    public StackEditorWindow() { InitializeComponent(); BranchList.ItemsSource = _branches; BranchList.AddHandler(DragDrop.DropEvent, DropLayer); BranchList.AddHandler(DragDrop.DragOverEvent, DragOverLayer); }
    public StackEditorWindow(RepositorySnapshot repository, StackDefinition? stack, Func<StackDefinition, Task<bool>> save) : this()
    {
        _save = save;
        var refs = repository.Refs.Select(r => r.Name).ToList();
        if (stack is not null)
        {
            _id = stack.Id; NameInput.Text = stack.Name;
            foreach (var branch in stack.Branches) _branches.Add(branch);
            if (!refs.Contains(stack.Base)) refs.Add(stack.Base);
        }
        BaseInput.ItemsSource = refs; BranchInput.ItemsSource = refs;
        BaseInput.SelectedItem = stack?.Base ?? refs.FirstOrDefault(r => r == "refs/heads/main") ?? refs.FirstOrDefault();
        DeleteButton.IsVisible = stack is not null;
    }
    private void AddClick(object? sender, RoutedEventArgs e)
    {
        if (BranchInput.SelectedItem is not string branch) return;
        if (_branches.Contains(branch) || Equals(BaseInput.SelectedItem, branch)) { ErrorLabel.Text = "A layer cannot repeat or equal the base."; return; }
        _branches.Add(branch); BranchList.SelectedIndex = _branches.Count - 1; ErrorLabel.Text = "";
    }
    private void UpClick(object? sender, RoutedEventArgs e) => Move(-1);
    private void DownClick(object? sender, RoutedEventArgs e) => Move(1);
    private void Move(int delta) { var index = BranchList.SelectedIndex; if (index < 0 || index + delta < 0 || index + delta >= _branches.Count) return; _branches.Move(index, index + delta); BranchList.SelectedIndex = index + delta; }
    private void RemoveClick(object? sender, RoutedEventArgs e) { if (BranchList.SelectedIndex >= 0) _branches.RemoveAt(BranchList.SelectedIndex); }
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void DeleteClick(object? sender, RoutedEventArgs e)
    {
        if (!DeleteRequested) { DeleteRequested = true; DeleteButton.Content = "Confirm delete"; ErrorLabel.Text = "Deletes this stack definition only. Branches are kept."; return; }
        Close(true);
    }
    private async void SaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            DeleteRequested = false;
            var stack = new StackDefinition(_id, NameInput.Text?.Trim() ?? "", BaseInput.SelectedItem as string ?? "", _branches.ToArray());
            StackValidation.Validate([stack]);
            IsEnabled = false;
            if (_save is not null && await _save(stack)) Close(true);
            else ErrorLabel.Text = "Unable to save. Close this editor and check the main window error. Your existing file is preserved.";
        }
        catch (Exception ex) { ErrorLabel.Text = ex.Message; }
        finally { IsEnabled = true; }
    }
#pragma warning disable CS0618 // Avalonia 11 compatibility; data is internal and never interpreted as commands.
    private async void LayerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: string branch } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var data = new DataObject(); data.Set("Stacker.Layer", branch);
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); }
        catch (Exception ex) { ErrorLabel.Text = ex.Message; }
    }
    private void DragOverLayer(object? sender, DragEventArgs e) { e.DragEffects = e.Data.Contains("Stacker.Layer") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; }
    private void DropLayer(object? sender, DragEventArgs e)
    {
        if (e.Data.Get("Stacker.Layer") is not string branch) return;
        var control = e.Source as Control;
        var item = control as ListBoxItem ?? control?.FindAncestorOfType<ListBoxItem>();
        var target = item?.DataContext as string;
        var from = _branches.IndexOf(branch); var to = target is null ? _branches.Count - 1 : _branches.IndexOf(target);
        if (from >= 0 && to >= 0) { _branches.Move(from, to); BranchList.SelectedIndex = to; }
        e.Handled = true;
    }
#pragma warning restore CS0618
}
