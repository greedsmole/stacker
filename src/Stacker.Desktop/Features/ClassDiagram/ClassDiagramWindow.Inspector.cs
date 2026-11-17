using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Core;

namespace Stacker.Desktop;

internal sealed partial class ClassDiagramWindow
{
    private const int DiagramBatch = 24;
    private const int DiagramLimit = 120;
    private CSharpGraph? _before;
    private CSharpGraph? _after;
    private Dictionary<string, CSharpTypeChange> _types = [];
    private Dictionary<string, CSharpMember> _members = [];
    private ILookup<string, CSharpRelationshipChange>? _neighbors;
    private CSharpTypeChange[] _changed = [];
    private CSharpTypeChange? _focused;
    private readonly List<string> _visible = [];
    private int _changePage;
    private bool _selectingType;
    private CSharpRelationshipKind? EdgeKind => (EdgeKinds.SelectedItem as FilterChoice)?.Kind;

    private void SetGraph(CSharpGraphComparison? graph, CSharpGraph? before, CSharpGraph? after)
    {
        _before = before;
        _after = after;
        _types = graph?.Types.ToDictionary(item => item.Type.Id, StringComparer.Ordinal) ?? [];
        _members = graph?.Types.SelectMany(type => type.Members)
            .ToDictionary(item => item.Member.Id, item => item.Member, StringComparer.Ordinal) ?? [];
        _neighbors = graph?.Relationships.SelectMany(edge =>
        {
            var relationship = edge.Relationship;
            return relationship.TargetType is { } target && target != relationship.SourceType
                ? new[] { (relationship.SourceType, edge), (target, edge) } : [(relationship.SourceType, edge)];
        }).ToLookup(item => item.Item1, item => item.edge, StringComparer.Ordinal);
        _changed = _types.Values.Where(item => item.Change is CSharpChange.Added or CSharpChange.Removed or CSharpChange.Modified).ToArray();
        _focused = null;
        _changePage = 0;
        _visible.Clear();
        CoverageButton.IsEnabled = after is not null;
        ChangesOnly.IsEnabled = before is not null;
        ChangesOnly.IsChecked = _changed.Length > 0;
        UpdateTypeList();
        UpdateInspector();

        if (graph is null)
        {
            DrawDiagram();
            return;
        }
        if (_changed.Length > 0)
            ShowChangePage();
        else if (_types.Values.FirstOrDefault() is { } first)
            FocusType(first);
        else
            DrawDiagram();

        var incomplete = after?.IsComplete != true || before?.IsComplete == false;
        Status.Text = incomplete
            ? $"Analysis is incomplete · {_types.Count} types available. Open Analysis details for coverage and errors."
            : before is null ? $"{_types.Count} types. Select a type to explore its members and relationships."
            : _changed.Length == 0 ? $"No C# semantic changes across this range · {_types.Count} types."
            : $"{_changed.Length} changed types of {_types.Count}. + Added   − Removed   ~ Modified";
    }

    private void TypeFilterChanged(object? sender, RoutedEventArgs e) => UpdateTypeList();

    private void UpdateTypeList()
    {
        var query = TypeQuery.Text?.Trim() ?? "";
        var candidates = ChangesOnly.IsChecked == true ? _changed.AsEnumerable() : _types.Values;
        _selectingType = true;
        TypeList.ItemsSource = candidates.Where(item => item.Type.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Type.Namespace.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Type.Project.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        TypeList.SelectedItem = _focused;
        _selectingType = false;
    }

    private void TypeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_selectingType && TypeList.SelectedItem is CSharpTypeChange item)
            FocusType(item);
    }

    private void FocusType(CSharpTypeChange type)
    {
        _focused = type;
        _selectingType = true;
        TypeList.SelectedItem = type;
        _selectingType = false;
        _visible.Clear();
        _visible.Add(type.Type.Id);
        AddNeighbors(_visible);
        UpdateInspector();
        DrawDiagram();
        Viewport.Offset = default;
    }

    private void ShowChangePage()
    {
        _focused = null;
        _selectingType = true;
        TypeList.SelectedItem = null;
        _selectingType = false;
        _visible.Clear();
        _visible.AddRange(_changed.Skip(_changePage * DiagramBatch).Take(DiagramBatch).Select(item => item.Type.Id));
        AddNeighbors(_visible);
        UpdateInspector();
        DrawDiagram();
        Viewport.Offset = default;
    }

    private void MoreClick(object? sender, RoutedEventArgs e)
    {
        _changePage = _focused is null ? (_changePage + 1) % ((_changed.Length + DiagramBatch - 1) / DiagramBatch) : 0;
        ShowChangePage();
    }

    private IEnumerable<CSharpRelationshipChange> Neighbors(string id) =>
        (_neighbors?[id] ?? []).Where(edge => EdgeKind is null || edge.Relationship.Kind == EdgeKind);

    private static string? OtherEnd(string id, CSharpRelationship edge) =>
        edge.SourceType == id ? edge.TargetType : edge.SourceType;

    private string[] NextNeighbors() => _visible.SelectMany(id => Neighbors(id).Select(edge => OtherEnd(id, edge.Relationship)))
        .OfType<string>().Distinct(StringComparer.Ordinal).Except(_visible, StringComparer.Ordinal).Take(DiagramBatch).ToArray();

    private void AddNeighbors(List<string> visible) => visible.AddRange(NextNeighbors().Take(DiagramLimit - visible.Count));

    private void ExpandClick(object? sender, RoutedEventArgs e)
    {
        AddNeighbors(_visible);
        DrawDiagram();
    }

    private void EdgeFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_focused is { } type)
            FocusType(type);
        else if (_changed.Length > 0)
            ShowChangePage();
    }

    private void UpdateInspector()
    {
        InspectorTitle.Text = _focused is { } type ? ChangePrefix(type.Change) + type.Type.Name : "Select a type";
        SourceButton.IsEnabled = NeighborButton.IsEnabled = false;
        MemberList.ItemsSource = _focused is { } focus ? SourceItems(focus).ToArray() : [];
        RelationshipList.ItemsSource = _focused is { } related ? RelationshipItems(related.Type.Id).ToArray() : [];
    }

    private IEnumerable<SourceItem> SourceItems(CSharpTypeChange focus)
    {
        if (focus.After is { } type && _after is { } head)
            yield return new(ChangePrefix(focus.Change) + type.Kind + " declaration", head.Commit, type.Locations);
        if (focus.Before is { } oldType && _before is { } basis && focus.Change != CSharpChange.Unchanged)
            yield return new("Base · " + oldType.Kind + " declaration", basis.Commit, oldType.Locations);
        foreach (var item in focus.Members)
        {
            if (item.After is { } member && _after is { } snapshot)
                yield return new(ChangePrefix(item.Change) + member.Signature, snapshot.Commit, member.Locations);
            if (item.Before is { } oldMember && _before is { } previous && item.Change != CSharpChange.Unchanged)
                yield return new((item.After is null ? ChangePrefix(item.Change) : "Base · ") + oldMember.Signature, previous.Commit, oldMember.Locations);
        }
    }

    private IEnumerable<RelationshipItem> RelationshipItems(string id)
    {
        foreach (var item in Neighbors(id).OrderBy(edge => edge.Relationship.Kind)
            .ThenBy(edge => edge.Relationship.TargetSignature, StringComparer.Ordinal))
        {
            var edge = item.Relationship;
            var outgoing = edge.SourceType == id;
            var source = edge.SourceMember is { } member && _members.TryGetValue(member, out var declaration)
                ? declaration.Signature : _types[edge.SourceType].Type.Name;
            var direction = outgoing ? "Outgoing" : "Incoming";
            yield return new($"{ChangePrefix(item.Change)}{KindLabel(edge.Kind)} · {direction}\n{source} → {edge.TargetSignature}", OtherEnd(id, edge));
        }
    }

    private void RelationshipSelected(object? sender, SelectionChangedEventArgs e) =>
        NeighborButton.IsEnabled = RelationshipList.SelectedItem is RelationshipItem { Neighbor: { } id } && _types.ContainsKey(id);

    private void NeighborClick(object? sender, RoutedEventArgs e)
    {
        if (RelationshipList.SelectedItem is RelationshipItem { Neighbor: { } id } && _types.TryGetValue(id, out var neighbor))
            FocusType(neighbor);
    }

    private void DetailSelected(object? sender, SelectionChangedEventArgs e) =>
        SourceButton.IsEnabled = MemberList.SelectedItem is SourceItem { Locations.Count: > 0 };

    private void SourceClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (MemberList.SelectedItem is not SourceItem { Locations: [var first, ..] } item)
            return;
        var request = new SymbolNavigationRequest(_context.Repository.Root, item.Commit, first.Path, first.Line, first.Column, SymbolNavigationKind.Definition);
        await DiffSectionView.ShowSymbolLocationsAsync(this, _main, request, new(item.Label, item.Locations, false), CancellationToken.None);
    }, message => Status.Text = message);

    private void CoverageClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        var close = new Button { Content = "Close", IsCancel = true };
        var content = new SelectableTextBlock
        {
            Text = string.Join("\n\n", new[] { (_before, "Base"), (_after, "Selected revision") }
                .Where(item => item.Item1 is not null).Select(item => CoverageText(item.Item1!, item.Item2))),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new(16)
        };
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Bottom);
        close.Margin = new(16, 0, 16, 16);
        close.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        layout.Children.Add(close);
        layout.Children.Add(new ScrollViewer { Content = content });
        var window = new Window { Title = "C# analysis details", Content = layout, Width = 720, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        close.Click += (_, _) => window.Close();
        await window.ShowDialog(this);
    }, message => Status.Text = message);

    private static string CoverageText(CSharpGraph graph, string side) => string.Join("\n",
        new[] { $"{side} · {graph.Commit}", graph.IsComplete ? "Projects analyzed" : "Incomplete analysis" }
            .Concat(graph.Projects).Concat(graph.Diagnostics));

    private static string ChangePrefix(CSharpChange change) => change switch
    {
        CSharpChange.Added => "+ ",
        CSharpChange.Removed => "− ",
        CSharpChange.Modified => "~ ",
        CSharpChange.Unknown => "? ",
        _ => ""
    };

    private static string KindLabel(CSharpRelationshipKind kind) => kind switch
    {
        CSharpRelationshipKind.UsesType => "Uses type",
        _ => kind.ToString()
    };
}
