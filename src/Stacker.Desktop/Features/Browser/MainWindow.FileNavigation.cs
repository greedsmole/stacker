using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Stacker.Desktop.ViewModels;

namespace Stacker.Desktop;

public partial class MainWindow
{
    private DiffSectionViewModel[] _fileSections = [];
    private DiffSectionViewModel? _activeFileSection;
    private DiffFileRow[] _observedFileStatuses = [];
    private bool _fileRefreshQueued;
    private bool _fileStatusRefreshQueued;

    private void InitializeFileNavigation()
    {
        SharedFiles.FileChosen += OpenFile;
        SharedFiles.FileReviewRequested += row =>
        {
            if (_fileSections is [var section] && _observedFileStatuses.Contains(row))
                AsyncHandler.Run(() => Vm.ToggleFileReviewedAsync(section, row),
                    message => AsyncHandler.Report(this, message));
        };
        SharedFiles.FolderModeChanged += folders => AsyncHandler.Run(() => Vm.SetGroupFilesByFolderAsync(folders),
            message => AsyncHandler.Report(this, message));
        SharedFiles.QueryChanged += query =>
        {
            foreach (var section in Vm.Sections)
                section.Filter = query;
        };
        Vm.Sections.CollectionChanged += FileSectionsChanged;
        Vm.PropertyChanged += FileNavigationPropertyChanged;
        Closed += (_, _) =>
        {
            Vm.Sections.CollectionChanged -= FileSectionsChanged;
            Vm.PropertyChanged -= FileNavigationPropertyChanged;
            DetachFileNavigation();
        };
        UpdateFileNavigation();
    }

    private void FileNavigationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.GroupFilesByFolder))
            SharedFiles.ApplyFolderMode(Vm.GroupFilesByFolder);
    }

    private void FileSectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_fileRefreshQueued)
            return;
        _fileRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _fileRefreshQueued = false;
            if (IsVisible)
                UpdateFileNavigation();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateFileNavigation()
    {
        DetachFileNavigation();
        _fileSections = Vm.Sections.ToArray();
        _activeFileSection = _fileSections.FirstOrDefault(section => section.IsActiveFileSection) ??
            _fileSections.FirstOrDefault(section => section.IsExpanded) ?? _fileSections.FirstOrDefault();

        var items = _fileSections.SelectMany((section, index) => section.AllFileRows.Select(row =>
            new FileNavigationItem(row.Change, row, index, section.Title))).ToArray();
        foreach (var section in _fileSections)
            section.PropertyChanged += FileSectionPropertyChanged;
        _observedFileStatuses = _fileSections.Length == 1 ? _fileSections[0].AllFileRows.ToArray() : [];
        foreach (var row in _observedFileStatuses)
            row.PropertyChanged += FileReviewStatusChanged;
        SharedFiles.SetFiles(items, _activeFileSection?.SelectedFileRow, Vm.Settings.GroupFilesByFolder,
            _fileSections.Length > 1, _activeFileSection?.Filter ?? "");
        if (_activeFileSection is { IsActiveFileSection: true, IsExpanded: true } active && _fileSections.Length > 1)
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_activeFileSection, active))
                    FindDiffSection(active)?.BringIntoView();
            }, DispatcherPriority.Loaded);
    }

    private void DetachFileNavigation()
    {
        foreach (var section in _fileSections)
            section.PropertyChanged -= FileSectionPropertyChanged;
        foreach (var row in _observedFileStatuses)
            row.PropertyChanged -= FileReviewStatusChanged;
    }

    private void FileSectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_fileRefreshQueued && e.PropertyName == nameof(DiffSectionViewModel.SelectedFile) && sender is DiffSectionViewModel section)
            ShowFileSelection(section);
    }

    private void FileReviewStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DiffFileRow.ReviewStatus) || _fileStatusRefreshQueued)
            return;
        _fileStatusRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _fileStatusRefreshQueued = false;
            if (IsVisible)
                SharedFiles.RefreshReviewStatus();
        }, DispatcherPriority.Loaded);
    }

    private void OpenFile(FileNavigationItem item)
    {
        if (item.SectionIndex >= _fileSections.Length || item.Target is not DiffFileRow row)
            return;
        var section = _fileSections[item.SectionIndex];
        if (!Vm.Sections.Contains(section))
            return;
        if (_reviewCompact)
        {
            StopReviewMotion();
            Vm.CloseReviewCommand.Execute(null);
        }
        section.IsExpanded = true;
        section.SelectedFileRow = row;
        ShowFileSelection(section);
        Dispatcher.UIThread.Post(() => FindDiffSection(section)?.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void ShowFileSelection(DiffSectionViewModel section)
    {
        if (_activeFileSection is { } previous && !ReferenceEquals(previous, section))
            previous.IsActiveFileSection = false;
        _activeFileSection = section;
        section.IsActiveFileSection = true;
        SharedFiles.SelectFile(section.SelectedFileRow);
        if (section is { IsPrSnapshot: true, SelectedFile: { } file, Review: { PanelTab: 1, IsBusy: false } review })
            review.SelectedReviewFile = file.NewPath;
    }

    private DiffSectionView? FindDiffSection(DiffSectionViewModel section) =>
        SectionScroll.GetVisualDescendants().OfType<DiffSectionView>().FirstOrDefault(view => ReferenceEquals(view.DataContext, section));

    private void PreviousFileClick(object? sender, RoutedEventArgs e) => SharedFiles.Step(-1);
    private void NextFileClick(object? sender, RoutedEventArgs e) => SharedFiles.Step(1);
}
