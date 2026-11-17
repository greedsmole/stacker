using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;

public sealed partial class HistoryPanel : UserControl
{
    public HistoryPanel() => AvaloniaXamlLoader.Load(this);

    private void OpenFileFolders(object? sender, EventArgs e)
    {
        if (DataContext is not HistoryWorkspaceViewModel history || sender is not Flyout { Content: Grid content })
            return;
        var picker = content.Children.OfType<TreeView>().Single();
        var tree = history.HasDocumentFiles
            ? FileFolderTree.Create(history.DocumentFiles, item => item.File,
                sectionFor: history.DocumentSections.Count > 1 ? item => (item.SectionIndex, history.DocumentSections[item.SectionIndex].Title) : null)
            : FileFolderTree.Create(history.Files, item => item.Change);
        var selected = tree.Find(history.HasDocumentFiles ? history.SelectedDocumentFile : (object?)history.SelectedFile);
        selected?.Reveal();
        picker.ItemsSource = tree.Roots;
        picker.SelectedItem = selected;
    }

    private void FolderFileTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source)
            return;
        var row = source as TreeViewItem ?? source.FindAncestorOfType<TreeViewItem>();
        if (row?.DataContext is FileTreeNode node && OpenFolderFile(node))
            e.Handled = true;
    }

    private void FolderFileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TreeView { SelectedItem: FileTreeNode node } && OpenFolderFile(node))
            e.Handled = true;
    }

    private bool OpenFolderFile(FileTreeNode node)
    {
        if (DataContext is not HistoryWorkspaceViewModel { IsBusy: false } history)
            return false;
        switch (node.Item)
        {
            case HistoryDocumentFile document when history.DocumentFiles.Contains(document):
                history.SelectedDocumentFile = document;
                break;
            case FileReviewStatus layer when history.Files.Contains(layer):
                history.SelectedFile = layer;
                break;
            default:
                return false;
        }
        this.FindControl<Button>("HistoryFolderButton")?.Flyout?.Hide();
        return true;
    }
}
