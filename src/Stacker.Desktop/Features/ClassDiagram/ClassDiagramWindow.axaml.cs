using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop.ViewModels;

namespace Stacker.Desktop;

internal sealed partial class ClassDiagramWindow : Window
{
    private readonly MainViewModel _main;
    private readonly DiagramTarget _target;
    private readonly ICSharpGraphReader _reader;
    private DiagramContext _context;
    private CancellationTokenSource? _load;
    private bool _updating;
    private bool _authorized;
    private bool _closed;
    private bool _refreshing;
    private string? _entry;
    private DiagramMode Mode => (Modes.SelectedItem as ModeChoice)?.Mode ?? DiagramMode.Layer;

    internal ClassDiagramWindow(MainViewModel main, DiagramTarget target)
    {
        _main = main;
        _target = target;
        _reader = main.CSharpGraph!;
        _context = main.CreateDiagramContext(target);
        InitializeComponent();
        TypeList.ItemTemplate = new FuncDataTemplate<CSharpTypeChange>((type, _) =>
        {
            if (type is null) return new TextBlock();
            var lines = new StackPanel { Spacing = 2 };
            lines.Children.Add(new TextBlock { Text = ChangePrefix(type.Change) + type.Type.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            lines.Children.Add(new TextBlock { Text = type.Type.Project, Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTip.SetTip(lines, $"{type.Type.Project}\n{type.Type.Name}");
            return lines;
        });
        MemberList.ItemTemplate = new FuncDataTemplate<SourceItem>((item, _) =>
            new TextBlock { Text = item?.Label, TextWrapping = TextWrapping.Wrap, FontFamily = new("Menlo,Consolas,monospace"), FontSize = 11 });
        RelationshipList.ItemTemplate = new FuncDataTemplate<RelationshipItem>((item, _) =>
            new TextBlock { Text = item?.Label, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        FilterChoice[] filters = [ new(null, "All relationships"), .. Enum.GetValues<CSharpRelationshipKind>()
            .Select(kind => new FilterChoice(kind, KindLabel(kind)))];
        EdgeKinds.ItemsSource = filters;
        EdgeKinds.SelectedIndex = 0;
        SetRevisions(_context.Current.Key, _context.DisplayedBase is null ? DiagramMode.Layer : DiagramMode.Displayed);
        ActualThemeVariantChanged += (_, _) => DrawDiagram();
        Viewport.SizeChanged += (_, _) => DrawDiagram();
        Opened += (_, _) => StartLoad();
        Closed += (_, _) => { _closed = true; _load?.Cancel(); };
    }

    private void SetRevisions(string key, DiagramMode mode)
    {
        _updating = true;
        Revisions.ItemsSource = _context.Revisions;
        Revisions.SelectedItem = _context.Revisions.FirstOrDefault(revision => revision.Key == key) ?? _context.Current;
        SetModes(mode);
        _updating = false;
    }

    private void SetModes(DiagramMode mode)
    {
        var revision = (DiagramRevision)Revisions.SelectedItem!;
        var modes = new List<ModeChoice>();
        if (revision.Key == _context.Current.Key && _context.DisplayedBase is not null)
            modes.Add(new(DiagramMode.Displayed, "Displayed changes"));
        if (revision.Parent is not null)
            modes.Add(new(DiagramMode.Layer, "This layer"));
        if (_context.StackBase is { } basis && revision.Parent is not null && basis.Commit != revision.Commit)
            modes.Add(new(DiagramMode.Cumulative, "From stack base"));
        modes.Add(new(DiagramMode.Snapshot, "Snapshot"));
        Modes.ItemsSource = modes;
        Modes.SelectedItem = modes.FirstOrDefault(item => item.Mode == mode) ?? modes[0];
    }

    private void RangeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _closed || Revisions.SelectedItem is not DiagramRevision || !ReferenceEquals(sender, e.Source))
            return;
        if (ReferenceEquals(sender, Revisions))
        {
            _updating = true;
            SetModes(Mode);
            _updating = false;
        }
        StartLoad();
    }

    private void ScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || Scopes.SelectedItem is not string entry) return;
        _entry = entry;
        if (_authorized)
            StartLoad();
        else
            LoadButton.IsEnabled = true;
    }

    private void LoadClick(object? sender, RoutedEventArgs e)
    {
        _authorized = true;
        StartLoad();
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => _load?.Cancel();
    private void CloseClick(object? sender, RoutedEventArgs e) => Close();

    private void RefreshClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        _load?.Cancel();
        _load = null;
        _refreshing = true;
        SetBusy(true);
        Status.Text = "Refreshing repository and PR revisions…";
        try
        {
            if (_context.GitHub is not null)
                await _main.RefreshAllAsync();
            else
                await _main.RefreshAsync();
            if (!_closed && _main.HasPendingChanges)
                await _main.UpdateChangesAsync();
            if (_closed) return;
            if (_main.HasError) throw new StackerException(_main.Error);
            var key = ((DiagramRevision)Revisions.SelectedItem!).Key;
            var mode = Mode;
            _context = _main.CreateDiagramContext(_target);
            SetRevisions(key, mode);
        }
        finally
        {
            _refreshing = false;
            if (!_closed) SetBusy(false);
        }
        if (!_closed) StartLoad();
    }, message => { if (!_closed) Status.Text = message; });

    private void StartLoad() => AsyncHandler.Run(LoadAsync, message => { if (!_closed) Status.Text = message; });

    private async Task LoadAsync()
    {
        if (_closed || _refreshing || Revisions.SelectedItem is not DiagramRevision head)
            return;
        _load?.Cancel();
        using var load = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(load.Token, deadline.Token);
        _load = load;
        var ct = linked.Token;
        var mode = Mode;
        var context = _context;
        var proposedBase = context.BaseFor(head, mode);
        Range.Text = RangeLabel(proposedBase, head);
        try
        {
            SetBusy(true);
            SetGraph(null, null, null);
            Status.Text = "Finding C# projects at the selected revisions…";
            var basis = await _main.PrepareDiagramRangeAsync(context, head, mode, ct);
            ct.ThrowIfCancellationRequested();
            var afterEntries = (await _reader.EntriesAsync(context.Repository, head.Commit, ct)).OrThrow();
            var beforeEntries = basis is null || basis.Commit == head.Commit
                ? afterEntries : (await _reader.EntriesAsync(context.Repository, basis.Commit, ct)).OrThrow();
            ct.ThrowIfCancellationRequested();
            Range.Text = RangeLabel(basis, head);
            var entries = afterEntries.Union(beforeEntries, StringComparer.Ordinal).ToArray();
            var common = afterEntries.Intersect(beforeEntries, StringComparer.Ordinal).ToArray();
            var preferred = common.Length == 0 ? entries : common;
            var solutions = preferred.Where(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).ToArray();
            _updating = true;
            Scopes.ItemsSource = entries;
            var remembered = _entry;
            _entry = remembered is not null && entries.Contains(remembered, StringComparer.Ordinal) ? remembered
                : solutions is [var solution] ? solution : preferred is [var single] ? single : null;
            Scopes.SelectedItem = _entry;
            _updating = false;
            if (entries.Length == 0)
            {
                Status.Text = "There is no checked-in C# solution or project at these revisions.";
                return;
            }
            if (_entry is null)
            {
                Status.Text = "Choose the solution or project to analyze.";
                return;
            }
            if (!_authorized)
            {
                Status.Text = "Analysis restores packages and runs MSBuild when you switch revisions or scopes. Analyze code you trust.";
                return;
            }
            var after = await ReadGraphAsync(context.Repository, head, _entry, ct);
            var before = basis is null ? null : basis.Commit == head.Commit ? after : await ReadGraphAsync(context.Repository, basis, _entry, ct);
            var comparison = await Task.Run(() => CSharpGraphComparison.Compare(before, after), ct);
            ct.ThrowIfCancellationRequested();
            SetGraph(comparison, before, after);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!_closed && ReferenceEquals(_load, load))
                Status.Text = deadline.IsCancellationRequested ? "C# analysis timed out. Retry or choose a smaller scope." : "C# analysis cancelled. Select Analyze C# to try again.";
        }
        catch (Exception ex)
        {
            if (!_closed && ReferenceEquals(_load, load))
                Status.Text = ex.Message;
        }
        finally
        {
            if (!_closed && ReferenceEquals(_load, load))
            {
                _load = null;
                if (!_refreshing) SetBusy(false);
            }
        }
    }

    private async Task<CSharpGraph> ReadGraphAsync(RepositoryIdentity repository, DiagramRevision revision, string entry, CancellationToken ct)
    {
        Status.Text = $"Loading {entry} at {revision}…";
        var result = await _reader.ReadAsync(new(repository, revision.Commit, entry), ct);
        ct.ThrowIfCancellationRequested();
        return result.IsOk ? result.OrThrow() : new(revision.Commit, [], [], [], [result.Problem!.Message]);
    }

    private void SetBusy(bool busy)
    {
        Progress.IsVisible = busy;
        CancelButton.IsVisible = busy && !_refreshing;
        LoadButton.IsEnabled = !busy && _entry is not null;
        RefreshButton.IsEnabled = !_refreshing;
        Scopes.IsEnabled = !busy;
        Revisions.IsEnabled = Modes.IsEnabled = !_refreshing;
    }

    private static string RangeLabel(DiagramRevision? basis, DiagramRevision head) =>
        basis is null ? head.ToString() : $"{basis} → {head}";
}
