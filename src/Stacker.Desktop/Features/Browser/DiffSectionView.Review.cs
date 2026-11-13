using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;

public partial class DiffSectionView
{
    private void ReviewClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (DataContext is not DiffSectionViewModel section || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main)
        return;
    if (!section.IsPrSnapshot)
    {
        await main.OpenPrChangesAsync(section);
        return;
    }
    await main.ShowReviewAsync(section);
}, message => AsyncHandler.Report(this, message));
    private void FileCommentClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () => { if (DataContext is DiffSectionViewModel section && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main) await main.ShowReviewAsync(section, 1); }, message => AsyncHandler.Report(this, message));
    private void FileReviewedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: DiffFileRow row } check ||
            DataContext is not DiffSectionViewModel section || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main)
            return;
        e.Handled = true;
        check.SetCurrentValue(ToggleButton.IsCheckedProperty, row.IsReviewed);
        AsyncHandler.Run(() => main.ToggleFileReviewedAsync(section, row), message => AsyncHandler.Report(this, message));
    }
    private void WrapCodeClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (sender is not ToggleButton toggle || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main)
        return;
    await main.SetWrapCodeAsync(toggle.IsChecked == true);
}, message => AsyncHandler.Report(this, message));
    private void CommentSelectedClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (DataContext is not DiffSectionViewModel section || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main)
        return;
    var selected = DiffLines.SelectedItems?.OfType<RenderedDiffLine>().Where(l => l.IsCode).ToHashSet() ?? [];
    if (selected.Count == 0)
    {
        section.Message = "Select adjacent code rows with Shift-click, then comment on the selection.";
        return;
    }
    if (selected.Any(line => !line.CanComment))
    {
        section.Message = "Select lines from the original diff to comment.";
        return;
    }
    var session = await main.EnsureReviewAsync(section);
    if (session is null || session.IsBusy)
        return;
    session.SetSelection(section.Lines.Where(selected.Contains));
    if (!session.HasSelection)
    {
        section.Message = session.Message;
        return;
    }
    foreach (var row in section.Lines)
        row.Editor = null;
    var last = section.Lines.Last(selected.Contains);
    last.Editor = session;
    main.ActiveReview = session;
    DiffLines.ScrollIntoView(last);
}, message => AsyncHandler.Report(this, message));
    private void AddLineClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (sender is not Control { DataContext: RenderedDiffLine line })
        return;
    var selected = ReferenceEquals(_commentLine, line) ? _commentSelection
        : DiffLines.SelectedItems?.OfType<RenderedDiffLine>().Where(l => l.IsCode).ToArray();
    var range = _extendRange;
    _commentLine = null;
    _commentSelection = null;
    _extendRange = false;
    // A plus inside the selected range comments on that range. A plus outside it starts a new single-line comment.
    await BeginLineAsync(line, range, selectedRows: selected is { Length: > 1 } && selected.Contains(line) ? selected : null);
}, message => AsyncHandler.Report(this, message));
    private void LineNumberPressed(object? sender, PointerPressedEventArgs e)
=> AsyncHandler.Run(async () => { if (sender is Control { DataContext: RenderedDiffLine { CanComment: true } line }) { e.Handled = true; await BeginLineAsync(line, e.KeyModifiers.HasFlag(KeyModifiers.Shift), Grid.GetColumn((Control)sender) == 1 ? "LEFT" : "RIGHT"); } }, message => AsyncHandler.Report(this, message));
    private async Task BeginLineAsync(RenderedDiffLine line, bool range, string? side = null, RenderedDiffLine[]? selectedRows = null)
    {
        if (DataContext is not DiffSectionViewModel section || !line.CanComment || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main)
            return;
        if (!section.IsPrSnapshot)
        {
            section.Message = section.HasPr ? "Review the PR snapshot to comment on code. Use Review PR in the file menu." : "Select a GitHub PR layer to comment on code.";
            return;
        }
        var session = await main.EnsureReviewAsync(section);
        if (session is null || session.IsBusy)
            return;
        var end = section.Lines.IndexOf(line);
        if (end < 0 || DataContext != section)
            return;
        // A range is only meaningful inside one file and across commentable rows: hunk headers and notices are skipped.
        var start = range && _rangeStart is { } first ? section.Lines.IndexOf(first) : end;
        if (start < 0)
            start = end;
        _rangeStart = section.Lines[start];
        var selected = (selectedRows ?? section.Lines.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1).ToArray())
            .Where(row => row.IsCode).ToArray();
        session.SetSelection(selected, side);
        if (!session.HasSelection)
        {
            section.Message = session.Message;
            return;
        }
        foreach (var row in section.Lines)
            row.Editor = null;
        line.Editor = session;
        main.ActiveReview = session;
        DiffLines.ScrollIntoView(line);
    }
    private void CloseInlineClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiffSectionViewModel section)
            foreach (var line in section.Lines)
                line.Editor = null;
    }
    private void EditInlineDraftClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (sender is not Control { DataContext: DraftComment draft } || DataContext is not DiffSectionViewModel section || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main)
        return;
    await main.ShowReviewAsync(section);
    main.ActiveReview?.EditDraftCommentCommand.Execute(draft);
}, message => AsyncHandler.Report(this, message));
    private void RetryClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (DataContext is DiffSectionViewModel vm)
        await vm.LoadAsync(vm.SelectedFile);
}, message => AsyncHandler.Report(this, message));
    private void CopyClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (DataContext is not DiffSectionViewModel vm)
        return;
    try
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.Patch);
    }
    catch (Exception ex) { vm.Message = "Cannot copy patch: " + ex.Message; }
}, message => AsyncHandler.Report(this, message));
}
