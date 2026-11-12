using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed record HistoryDocumentFile(int SectionIndex, FileChange File, string Label);

public sealed partial class HistoryWorkspaceViewModel
{
    public ObservableCollection<DiffSectionViewModel> DocumentSections { get; } = [];
    public DiffDocument? Document { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDocumentFiles))]
    private IReadOnlyList<HistoryDocumentFile> _documentFiles = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveDocumentSection)), NotifyPropertyChangedFor(nameof(HasActiveDocumentSection))]
    private HistoryDocumentFile? _selectedDocumentFile;
    public bool HasDocumentFiles => DocumentFiles.Count > 0;
    public DiffSectionViewModel? ActiveDocumentSection => SelectedDocumentFile is { } file ? DocumentSections[file.SectionIndex] : null;
    public bool HasActiveDocumentSection => ActiveDocumentSection is not null;
    private bool _wrapCode;
    internal bool WrapCode
    {
        get => _wrapCode;
        set
        {
            _wrapCode = value;
            foreach (var section in DocumentSections) section.WrapCode = value;
        }
    }
    private readonly Stack<NavigationTarget> _back = [];
    private readonly Stack<NavigationTarget> _forward = [];
    private NavigationTarget? _navigation;
    private NavigationTarget? _restoringNavigation;
    public bool CanGoBack => !IsBusy && _back.Count > 0;
    public bool CanGoForward => !IsBusy && _forward.Count > 0;
    partial void OnIsBusyChanged(bool value) { NotifyNavigation(); NotifySelectionCommands(); }
    private void NotifyNavigation() { BackCommand.NotifyCanExecuteChanged(); ForwardCommand.NotifyCanExecuteChanged(); }
    private void RememberNavigation(NavigationTarget target)
    {
        if (_restoringNavigation is not null) return;
        if (_navigation is not null && _navigation != target) { _back.Push(_navigation); _forward.Clear(); }
        _navigation = target; NotifyNavigation();
    }
    private void ClearDocument()
    {
        SelectedDocumentFile = null;
        DocumentFiles = [];
        foreach (var section in DocumentSections) section.Dispose();
        DocumentSections.Clear();
        Document = null;
        OnPropertyChanged(nameof(Document));
    }

    private async Task ShowDocumentAsync(DiffDocument document, string? path = null, string? versionId = null, int? preferredSection = null)
    {
        var layerPath = SelectedFile?.Change.NewPath;
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        try
        {
            ClearDocument();
            Document = document;
            var files = new List<HistoryDocumentFile>();
            for (var index = 0; index < document.Sections.Count; index++)
            {
                var item = document.Sections[index];
                var result = new DiffResult(item.Title, item.Title, item.BaseSha, item.HeadSha, null, item.Files);
                DocumentSections.Add(new(_git, item.Root, result, _highlighter, Theme, readOnly: true, selectFirstFile: false)
                    { WrapCode = WrapCode });
                foreach (var file in item.Files)
                {
                    var label = document.Mode == DiffDocumentMode.Current ? file.DisplayPath : $"{item.Title} · {file.DisplayPath}";
                    files.Add(new(index, file, label));
                }
            }
            DocumentFiles = files;
            var restored = _restoringNavigation;
            SelectedDocumentFile = files.FirstOrDefault(file => file.SectionIndex == restored?.SectionIndex && file.File.NewPath == restored?.SectionPath)
                ?? files.FirstOrDefault(file => file.File.NewPath == path && (preferredSection is null || file.SectionIndex == preferredSection))
                ?? files.FirstOrDefault();
        }
        finally { _loadingSelection = wasLoading; }

        if (_current is not null)
        {
            RememberNavigation(new(_current.ChangeId, versionId, layerPath, document.Mode)
            {
                SectionIndex = SelectedDocumentFile?.SectionIndex,
                SectionPath = SelectedDocumentFile?.File.NewPath
            });
        }
        OnPropertyChanged(nameof(Document));
        if (ActiveDocumentSection is { } section) await section.SelectedFileLoad;
    }

    partial void OnSelectedDocumentFileChanged(HistoryDocumentFile? value)
    {
        if (value is null)
        {
            NotifySelectionCommands();
            return;
        }

        DocumentSections[value.SectionIndex].SelectedFile = value.File;
        var layerFile = Files.FirstOrDefault(file => file.Change.NewPath == value.File.NewPath);
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        try { SelectedFile = layerFile; }
        finally { _loadingSelection = wasLoading; }
        NotifySelectionCommands();
        if (_loadingSelection) return;
        if (_navigation is { } entry)
            RememberNavigation(entry with
            {
                Path = entry.Mode == DiffDocumentMode.FileEvolution ? entry.Path : layerFile?.Change.NewPath ?? entry.Path,
                SectionIndex = value.SectionIndex,
                SectionPath = value.File.NewPath
            });
        if (layerFile is not null && SelectedNode is { } node) _ = SyncSelectionAsync(node, layerFile.Change);
    }

    private void SelectLayerFile(ChangeNode node, FileChange? file)
    {
        var match = file is null ? null : DocumentFiles.FirstOrDefault(entry => entry.File.NewPath == file.NewPath);
        if (match is not null && SelectedDocumentFile != match) SelectedDocumentFile = match;
        else _ = SyncSelectionAsync(node, file);
    }
    [RelayCommand(CanExecute = nameof(CanGoBack))] private async Task BackAsync() => await NavigateAsync(_back, _forward);
    [RelayCommand(CanExecute = nameof(CanGoForward))] private async Task ForwardAsync() => await NavigateAsync(_forward, _back);
    private async Task NavigateAsync(Stack<NavigationTarget> source, Stack<NavigationTarget> destination)
    {
        if (IsBusy || !source.TryPop(out var target)) return;
        var previous = _navigation; _restoringNavigation = target;
        try
        {
            SelectedNode = Nodes.FirstOrDefault(n => n.Id == target.ChangeId);
            if (!await LoadLayerDataAsync(target.Path)) { source.Push(target); return; }
            PreviousVersion = Versions.FirstOrDefault(v => v.Id == target.VersionId);
            if (target.Mode == DiffDocumentMode.FileEvolution) await EvolutionAsync();
            else if (target.Mode is DiffDocumentMode.BetweenVersions or DiffDocumentMode.SinceReviewed) await CompareVersionDocumentAsync(target.Mode);
            else await ShowVersionAsync(_current?.Id == target.VersionId ? _current : Versions.FirstOrDefault(v => v.Id == target.VersionId) ?? _current);
            // Restoring navigation is itself not a new navigation branch.
            if (previous is not null) destination.Push(previous); _navigation = target;
        }
        finally { _restoringNavigation = null; NotifyNavigation(); }
    }
}
