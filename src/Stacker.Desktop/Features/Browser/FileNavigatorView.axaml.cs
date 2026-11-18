using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Stacker.Desktop.ViewModels;

namespace Stacker.Desktop;

public sealed partial class FileNavigatorView : UserControl
{
    public static readonly DirectProperty<FileNavigatorView, bool> CanGoPreviousProperty =
        AvaloniaProperty.RegisterDirect<FileNavigatorView, bool>(nameof(CanGoPrevious), view => view.CanGoPrevious);
    public static readonly DirectProperty<FileNavigatorView, bool> CanGoNextProperty =
        AvaloniaProperty.RegisterDirect<FileNavigatorView, bool>(nameof(CanGoNext), view => view.CanGoNext);
    public static readonly DirectProperty<FileNavigatorView, bool> HasMultipleFilesProperty =
        AvaloniaProperty.RegisterDirect<FileNavigatorView, bool>(nameof(HasMultipleFiles), view => view.HasMultipleFiles);

    private FileNavigationItem[] _items = [];
    private FileNavigationItem[] _visible = [];
    private FileNavigationItem? _selected;
    private FileFolderTree? _tree;
    private bool _updating;
    private bool _folders;
    private bool _groupBySection;
    private bool _canGoPrevious;
    private bool _canGoNext;
    private bool _hasMultipleFiles;
    private string _query = "";

    public bool CanGoPrevious => _canGoPrevious;
    public bool CanGoNext => _canGoNext;
    public bool HasMultipleFiles => _hasMultipleFiles;
    internal event Action<FileNavigationItem>? FileChosen;
    internal event Action<DiffFileRow>? FileReviewRequested;
    internal event Action<bool>? FolderModeChanged;
    internal event Action<string>? QueryChanged;

    public FileNavigatorView() => InitializeComponent();

    internal void SetFiles(FileNavigationItem[] items, object? selected, bool folders, bool groupBySection, string query)
    {
        _items = items;
        SetAndRaise(HasMultipleFilesProperty, ref _hasMultipleFiles, items.Length > 1);
        _folders = folders;
        _groupBySection = groupBySection;
        _selected = Find(selected);
        _query = query;
        _updating = true;
        FilterBox.Text = query;
        if (groupBySection || _items.Length == 0 || _items.Any(item => item.ReviewRow?.HasReviewStatus != true))
            UnreviewedOnly.IsChecked = false;
        _updating = false;
        Rebuild();
    }

    internal void SelectFile(object? target)
    {
        var selected = Find(target);
        if (!ReferenceEquals(_selected, selected))
        {
            _selected = selected;
            ApplySelection();
        }
    }

    internal void RefreshReviewStatus()
    {
        // When the filter is active, marking a file reviewed removes its row without changing the open diff.
        if (UnreviewedOnly.IsChecked == true)
            Rebuild();
        else
            UpdateLabels();
    }

    internal void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    internal void Step(int direction)
    {
        var index = Array.IndexOf(_visible, _selected);
        var next = index < 0 ? (direction > 0 ? 0 : _visible.Length - 1) : index + direction;
        if (next >= 0 && next < _visible.Length)
            Choose(_visible[next], synchronize: true);
    }

    private FileNavigationItem? Find(object? target) => _items.FirstOrDefault(item => ReferenceEquals(item.Target, target));

    private void ListModeClick(object? sender, RoutedEventArgs e) => SetMode(false);
    private void FoldersModeClick(object? sender, RoutedEventArgs e) => SetMode(true);
    private void SetMode(bool folders)
    {
        if (ApplyFolderMode(folders))
            FolderModeChanged?.Invoke(folders);
        else
        {
            FoldersMode.IsChecked = _folders;
            ListMode.IsChecked = !_folders;
        }
    }

    internal bool ApplyFolderMode(bool folders)
    {
        if (_folders == folders)
            return false;
        _folders = folders;
        Rebuild();
        return true;
    }

    private void FileReviewedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: DiffFileRow row } check)
            return;
        e.Handled = true;
        check.SetCurrentValue(ToggleButton.IsCheckedProperty, row.IsReviewed);
        FileReviewRequested?.Invoke(row);
    }

    private void ClearFilterClick(object? sender, RoutedEventArgs e)
    {
        UnreviewedOnly.IsChecked = false;
        FilterBox.Text = "";
        UpdateQuery();
        FocusFilter();
    }

    private void FilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_updating)
            UpdateQuery();
    }

    private void UpdateQuery()
    {
        var query = FilterBox.Text ?? "";
        if (query == _query)
        {
            // Clearing the filters can also reset Unreviewed without changing the text.
            if (ClearFilter.IsVisible != (_query.Length > 0 || UnreviewedOnly.IsChecked == true))
                Rebuild();
            return;
        }
        _query = query;
        Rebuild();
        QueryChanged?.Invoke(query);
    }

    private void UnreviewedClick(object? sender, RoutedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var unreviewed = UnreviewedOnly.IsChecked == true;
        _visible = _items.Where(item => (!unreviewed || item.ReviewRow is { HasReviewStatus: true, IsReviewed: false })
                              && item.DisplayPath.Contains(_query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var useTree = _folders || _groupBySection;
        _tree = useTree ? FileFolderTree.Create(_visible, item => item.Change, expandFolders: _query.Length > 0 || unreviewed,
            sectionFor: _groupBySection ? item => (item.SectionIndex, item.SectionTitle) : null, groupByFolder: _folders) : null;
        if (_folders && _tree is not null)
            _visible = FilesInOrder(_tree.Roots).ToArray();

        _updating = true;
        FoldersMode.IsChecked = _folders;
        ListMode.IsChecked = !_folders;
        FileList.IsVisible = !useTree;
        FileList.ItemsSource = useTree ? null : _visible;
        FileTree.IsVisible = useTree;
        FileTree.ItemsSource = _tree?.Roots;
        _updating = false;

        UpdateLabels();
        ApplySelection();
    }

    private void UpdateLabels()
    {
        var hasProgress = !_groupBySection && _items.Length > 0 && _items.All(item => item.ReviewRow?.HasReviewStatus == true);
        UnreviewedOnly.IsVisible = hasProgress;
        FilesHeading.Text = $"Files ({_items.Length})";
        FileProgress.Text = hasProgress ? $"{_items.Count(item => item.ReviewRow!.IsReviewed)} of {_items.Length} reviewed here"
            : _visible.Length != _items.Length ? $"{_visible.Length} matching" : "";
        ClearFilter.IsVisible = _query.Length > 0 || UnreviewedOnly.IsChecked == true;
        FilesMessage.Text = _items.Length == 0 ? "No changed files in this comparison."
            : _visible.Length == 0 ? "No files match. Clear filters to see all changed files." : "";
        FilesMessage.IsVisible = FilesMessage.Text.Length > 0;
    }

    private void ApplySelection()
    {
        _updating = true;
        if (_tree is { } tree)
        {
            var node = tree.Find(_selected);
            node?.Reveal();
            FileTree.SelectedItem = node;
        }
        else
        {
            FileList.SelectedItem = _visible.Contains(_selected) ? _selected : null;
            if (FileList.SelectedItem is { } row)
                FileList.ScrollIntoView(row);
        }
        _updating = false;
        UpdateSteps();
    }

    private void UpdateSteps()
    {
        var index = Array.IndexOf(_visible, _selected);
        SetAndRaise(CanGoPreviousProperty, ref _canGoPrevious, index > 0);
        SetAndRaise(CanGoNextProperty, ref _canGoNext, index < _visible.Length - 1);
    }

    private void FlatSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updating && ReferenceEquals(e.Source, FileList) && FileList.SelectedItem is FileNavigationItem file)
            Choose(file);
    }

    private void TreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updating && ReferenceEquals(e.Source, FileTree) && FileTree.SelectedItem is FileTreeNode { Item: FileNavigationItem file })
            Choose(file);
    }

    private void Choose(FileNavigationItem file, bool synchronize = false)
    {
        _selected = file;
        if (synchronize)
            ApplySelection();
        else
            UpdateSteps();
        FileChosen?.Invoke(file);
    }

    private static IEnumerable<FileNavigationItem> FilesInOrder(IEnumerable<FileTreeNode> nodes)
    {
        foreach (var node in nodes)
            if (node.Item is FileNavigationItem file)
                yield return file;
            else
                foreach (var child in FilesInOrder(node.Children))
                    yield return child;
    }
}
