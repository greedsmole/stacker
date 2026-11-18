using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;

public sealed partial class HistoryPanel : UserControl
{
    private string _fileQuery = "";
    private bool _folders;

    public HistoryPanel() => AvaloniaXamlLoader.Load(this);

    private void OpenHistoryFiles(object? sender, EventArgs e)
    {
        if (DataContext is not HistoryWorkspaceViewModel history || sender is not Flyout { Content: FileNavigatorView picker })
            return;
        var main = (TopLevel.GetTopLevel(this) as MainWindow)?.DataContext as MainViewModel;
        picker.FileChosen -= SelectHistoryFile;
        picker.FileChosen += SelectHistoryFile;
        picker.FolderModeChanged -= HistoryFoldersChanged;
        picker.FolderModeChanged += HistoryFoldersChanged;
        picker.QueryChanged -= HistoryQueryChanged;
        picker.QueryChanged += HistoryQueryChanged;
        var items = history.HasDocumentFiles
            ? history.DocumentFiles.Select(item => new FileNavigationItem(item.File, item, item.SectionIndex,
                history.DocumentSections[item.SectionIndex].Title)).ToArray()
            : history.Files.Select(item => new FileNavigationItem(item.Change, item)).ToArray();
        picker.SetFiles(items, history.HasDocumentFiles ? history.SelectedDocumentFile : (object?)history.SelectedFile,
            main?.GroupFilesByFolder ?? _folders, history.DocumentSections.Count > 1, _fileQuery);
        Dispatcher.UIThread.Post(picker.FocusFilter, DispatcherPriority.Loaded);
    }

    private void HistoryQueryChanged(string query) => _fileQuery = query;
    private void HistoryFoldersChanged(bool folders)
    {
        _folders = folders;
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
            AsyncHandler.Run(() => main.SetGroupFilesByFolderAsync(folders), message => AsyncHandler.Report(this, message));
    }

    private void HistoryFilesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Source is Control control &&
            (control is ListBox { SelectedItem: not null } or TreeView { SelectedItem: FileTreeNode { IsFolder: false } } or
                 ListBoxItem or TreeViewItem || control.FindAncestorOfType<ListBoxItem>() is not null ||
             control.FindAncestorOfType<TreeViewItem>() is not null))
        {
            this.FindControl<Button>("HistoryFilesButton")?.Flyout?.Hide();
            e.Handled = true;
        }
    }

    private void HistoryFilesTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control control)
            return;
        var flat = control as ListBoxItem ?? control.FindAncestorOfType<ListBoxItem>();
        var tree = control as TreeViewItem ?? control.FindAncestorOfType<TreeViewItem>();
        if (flat?.DataContext is FileNavigationItem || tree?.DataContext is FileTreeNode { IsFolder: false })
            this.FindControl<Button>("HistoryFilesButton")?.Flyout?.Hide();
    }

    private void SelectHistoryFile(FileNavigationItem item)
    {
        if (DataContext is not HistoryWorkspaceViewModel { IsBusy: false } history)
            return;
        switch (item.Target)
        {
            case HistoryDocumentFile document when history.DocumentFiles.Contains(document):
                history.SelectedDocumentFile = document;
                break;
            case FileReviewStatus layer when history.Files.Contains(layer):
                history.SelectedFile = layer;
                break;
        }
    }
}
