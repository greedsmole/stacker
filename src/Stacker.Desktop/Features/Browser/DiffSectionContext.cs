using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public sealed partial class DiffSectionViewModel
{
    private const string FullContextTooLarge = "The full diff exceeds the display limit. Expand individual regions to read more.";
    private const string ContextAtLimit = "This diff reached the display limit. Collapse context to open another region.";
    private DiffContext? _context;
    private RenderedDiffLine[] _patchRows = [];
    private IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>>? _contextTokens;
    private CancellationTokenSource? _contextExpansion;
    private int _hiddenContextLines;

    [ObservableProperty] private bool _contextUnavailable;
    public bool HasCollapsedContext => _hiddenContextLines > 0;
    public bool HasExpandedContext => _context is { } context && _hiddenContextLines < context.HiddenLineCount;

    private void ResetContext()
    {
        _contextExpansion?.Cancel();
        _contextExpansion = null;
        _context = null;
        _contextTokens = null;
        _patchRows = [];
        ContextUnavailable = false;
        SetHiddenContextLines(0);
    }

    private void SetContext(DiffContext? context, RenderedDiffLine[] rows, bool unavailable)
    {
        _context = context;
        ContextUnavailable = unavailable;
        _patchRows = context is null ? [] : rows;
        Lines.ReplaceAll(context is null ? rows : FoldedRows(context));
        SetHiddenContextLines(context?.HiddenLineCount ?? 0);
    }

    private IEnumerable<RenderedDiffLine> FoldedRows(DiffContext context)
    {
        var next = 0;
        foreach (var gap in context.Gaps)
        {
            while (next < gap.BeforeRow)
                yield return _patchRows[next++];
            yield return new(new(DiffLineKind.Notice, null, null, ""))
            {
                ContextGap = gap,
                AllowComment = false
            };
        }
        while (next < _patchRows.Length)
            yield return _patchRows[next++];
    }

    internal RenderedDiffLine? ExpandContext(RenderedDiffLine row, bool fromEnd)
    {
        if (_context is not { } context || _contextExpansion is not null || row.ContextGap is not { } gap)
            return null;
        var index = Lines.IndexOf(row);
        if (index < 0)
            return null;
        var count = gap.RevealCount;
        var remaining = gap.Count - count;
        if (Lines.Count + count - (remaining == 0 ? 1 : 0) > DiffView.DisplayLineLimit)
        {
            Message = ContextAtLimit;
            return null;
        }

        fromEnd |= gap.RevealFromEnd;
        var offset = fromEnd ? remaining : 0;
        var rows = new RenderedDiffLine[count];
        for (var i = 0; i < count; i++)
            rows[i] = CreateContextLine(context, gap, offset + i);
        ApplyContextAppearance(rows);

        if (remaining == 0)
            Lines.RemoveAt(index);
        else if (fromEnd)
        {
            row.ContextGap = gap with
            {
                Count = remaining
            };
            index++;
        }
        else
            row.ContextGap = gap with
            {
                OldStart = gap.OldStart + count,
                NewStart = gap.NewStart + count,
                Count = remaining
            };
        foreach (var revealed in rows)
            Lines.Insert(index++, revealed);
        SetHiddenContextLines(_hiddenContextLines - count);
        ClearContextMessage();
        ApplyThreads();
        return rows[0];
    }

    internal async Task<bool> ExpandAllContextAsync()
    {
        if (_context is not { } context || !HasCollapsedContext || _contextExpansion is not null)
            return false;
        var total = _patchRows.Length + context.HiddenLineCount;
        if (total > DiffView.DisplayLineLimit)
        {
            Message = FullContextTooLarge;
            return false;
        }

        using var expansion = new CancellationTokenSource();
        _contextExpansion = expansion;
        var snapshot = Lines.ToArray();
        try
        {
            var expanded = await Task.Run(() =>
            {
                var rows = new List<RenderedDiffLine>(total);
                foreach (var row in snapshot)
                {
                    expansion.Token.ThrowIfCancellationRequested();
                    if (row.ContextGap is not { } gap)
                        rows.Add(row);
                    else
                    {
                        for (var i = 0; i < gap.Count; i++)
                        {
                            if (i % 1024 == 0)
                                expansion.Token.ThrowIfCancellationRequested();
                            rows.Add(CreateContextLine(context, gap, i));
                        }
                    }
                }
                return rows;
            }, expansion.Token);
            if (expansion.IsCancellationRequested || !ReferenceEquals(_context, context))
                return false;
            ApplyContextAppearance(expanded);
            Lines.ReplaceAll(expanded);
            SetHiddenContextLines(0);
            ClearContextMessage();
            ApplyThreads();
            return true;
        }
        catch (OperationCanceledException) when (expansion.IsCancellationRequested) { return false; }
        finally { if (ReferenceEquals(_contextExpansion, expansion)) _contextExpansion = null; }
    }

    internal void CollapseContext()
    {
        if (_context is not { } context || !HasExpandedContext)
            return;
        _contextExpansion?.Cancel();
        Lines.ReplaceAll(FoldedRows(context));
        SetHiddenContextLines(context.HiddenLineCount);
        ClearContextMessage();
        ApplyThreads();
    }

    private RenderedDiffLine CreateContextLine(DiffContext context, DiffContextGap gap, int offset) =>
        new(context.Line(gap.OldStart + offset, gap.NewStart + offset))
        {
            IsExpandedContext = true,
            AllowComment = !IsReadOnlyComparison
        };

    private void ApplyContextAppearance(IEnumerable<RenderedDiffLine> rows)
    {
        var wrapping = WrapCode ? TextWrapping.Wrap : TextWrapping.NoWrap;
        foreach (var row in rows)
        {
            if (!row.IsExpandedContext)
                continue;
            row.TextWrapping = wrapping;
            if (row.NewLineNumber is int number && _contextTokens?.TryGetValue(number, out var spans) == true)
                row.Tokens = spans;
        }
    }

    private void SetHiddenContextLines(int count)
    {
        _hiddenContextLines = count;
        OnPropertyChanged(nameof(HasCollapsedContext));
        OnPropertyChanged(nameof(HasExpandedContext));
    }

    private void ClearContextMessage()
    {
        if (Message is FullContextTooLarge or ContextAtLimit)
            Message = "";
    }
}
