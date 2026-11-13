using CommunityToolkit.Mvvm.ComponentModel;

namespace Stacker.Desktop.ViewModels;

public sealed partial class DiffSectionViewModel
{
    private FileFolderTree? _folderTree;
    private FileFolderTree? _unfilteredFolderTree;

    [ObservableProperty] private bool _groupFilesByFolder;
    public IReadOnlyList<FileTreeNode> FolderFiles => _folderTree?.Roots ?? [];
    public string FileCountLabel => $"Files ({Files.Count})";
    public bool HasNoFileMatches => Files.Count == 0 && Result.Files.Count > 0;
    public FileTreeNode? SelectedFolderFile
    {
        get => _folderTree?.Find(SelectedFileRow);
        set
        {
            if (GroupFilesByFolder && value?.ReviewRow is { } row)
                SelectedFileRow = row;
        }
    }

    partial void OnGroupFilesByFolderChanged(bool value) => RebuildFolderFiles();

    private void RebuildFolderFiles()
    {
        if (GroupFilesByFolder)
            _folderTree = Filter.Length == 0
                ? _unfilteredFolderTree ??= FileFolderTree.Create(VisibleFiles, row => row.Change)
                : FileFolderTree.Create(VisibleFiles, row => row.Change, expandFolders: true);
        else
            _folderTree = null;

        OnPropertyChanged(nameof(FolderFiles));
        UpdateFolderSelection();
    }

    private void UpdateFolderSelection()
    {
        SelectedFolderFile?.Reveal();
        OnPropertyChanged(nameof(SelectedFolderFile));
    }
}
