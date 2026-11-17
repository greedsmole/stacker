using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Stacker.Core;

namespace Stacker.Desktop;

internal sealed partial class ClassDiagramWindow
{
    private const double NodeWidth = 242;
    private const double NodeHeight = 112;
    private const double GapX = 64;
    private const double GapY = 56;
    private const double CanvasPadding = 30;
    private const int PreviewMembers = 3;
    private double _zoom = 1;
    private Point? _panStart;
    private Vector _panOffset;

    private void DrawDiagram()
    {
        GraphCanvas.Children.Clear();
        var columns = Math.Clamp((int)((Viewport.Bounds.Width - 2 * CanvasPadding + GapX) / (NodeWidth + GapX)), 1, 4);
        var nodes = new Dictionary<string, Rect>(StringComparer.Ordinal);
        for (var index = 0; index < _visible.Count; index++)
            nodes[_visible[index]] = new(CanvasPadding + index % columns * (NodeWidth + GapX),
                CanvasPadding + index / columns * (NodeHeight + GapY), NodeWidth, NodeHeight);
        GraphCanvas.Width = nodes.Count == 0 ? 1 : nodes.Values.Max(rect => rect.Right) + CanvasPadding;
        GraphCanvas.Height = nodes.Count == 0 ? 1 : nodes.Values.Max(rect => rect.Bottom) + CanvasPadding;

        var edges = _visible.SelectMany(Neighbors).Distinct()
            .Where(item => item.Relationship.TargetType is { } target && target != item.Relationship.SourceType
                && nodes.ContainsKey(target) && nodes.ContainsKey(item.Relationship.SourceType))
            .GroupBy(item => (item.Relationship.SourceType, Target: item.Relationship.TargetType!));
        foreach (var edge in edges)
            DrawEdge(nodes[edge.Key.SourceType], nodes[edge.Key.Target], edge.ToArray());
        foreach (var (id, bounds) in nodes)
            DrawType(_types[id], bounds);

        ExpandButton.IsEnabled = _visible.Count < DiagramLimit && NextNeighbors().Length > 0;
        MoreButton.IsVisible = _changed.Length > 0 && (_focused is not null || _changed.Length > DiagramBatch);
        MoreButton.Content = _focused is not null ? "Show changes"
            : $"Changes {_changePage + 1}/{(_changed.Length + DiagramBatch - 1) / DiagramBatch}";
    }

    private void DrawType(CSharpTypeChange item, Rect bounds)
    {
        var type = item.Type;
        var rows = new StackPanel { Spacing = 3 };
        var title = new TextBlock { Text = ChangePrefix(item.Change) + type.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        if (item.Change is CSharpChange.Added or CSharpChange.Removed or CSharpChange.Modified)
            title.Foreground = Brush(ChangeBrush(item.Change));
        rows.Children.Add(title);
        rows.Children.Add(new TextBlock
        {
            Text = $"{type.Kind} · {type.Members.Count} {(type.Members.Count == 1 ? "member" : "members")}",
            FontSize = 11, Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis
        });
        foreach (var member in item.Members.OrderBy(member => member.Change == CSharpChange.Unchanged).Take(PreviewMembers))
            rows.Children.Add(new TextBlock
            {
                Text = ChangePrefix(member.Change) + member.Member.Signature, FontSize = 10,
                FontFamily = new("Menlo,Consolas,monospace"), TextTrimming = TextTrimming.CharacterEllipsis
            });
        var button = new Button
        {
            Width = bounds.Width, Height = bounds.Height, Padding = new(10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
            Content = rows, BorderThickness = new Thickness(1), CornerRadius = new(3)
        };
        var selected = _focused?.Type.Id == type.Id;
        button.Background = Brush(selected ? "SelectionBrush" : "PanelBrush");
        button.BorderBrush = Brush(selected ? "PrimaryBrush" : "DividerBrush");
        ToolTip.SetTip(button, $"{type.Project}\n{type.Name}\n{item.Change}");
        Avalonia.Automation.AutomationProperties.SetName(button, title.Text);
        button.Click += (_, _) => FocusType(item);
        Canvas.SetLeft(button, bounds.X);
        Canvas.SetTop(button, bounds.Y);
        GraphCanvas.Children.Add(button);
    }

    private void DrawEdge(Rect from, Rect to, CSharpRelationshipChange[] edges)
    {
        Vector direction = to.Center - from.Center;
        var unit = direction.Normalize();
        var shift = new Vector(-unit.Y, unit.X) * 5;
        var start = BoundaryOf(from, direction) + shift;
        var end = BoundaryOf(to, -direction) + shift;
        var changes = edges.Select(edge => edge.Change).Distinct().ToArray();
        var brush = ChangeBrush(changes is [var change] ? change : CSharpChange.Modified);
        var line = new Line { StartPoint = start, EndPoint = end, StrokeThickness = 1.4 };
        line.Stroke = Brush(brush);
        if (edges.All(edge => edge.Change == CSharpChange.Removed))
            line.StrokeDashArray = [4, 3];
        ToolTip.SetTip(line, string.Join(" · ", edges.GroupBy(edge => edge.Relationship.Kind)
            .Select(group => $"{KindLabel(group.Key)} ({group.Count()})")));
        GraphCanvas.Children.Add(line);
        var back = end - unit * 10;
        var arrow = new Polygon { Points = [end, back + shift, back - shift], IsHitTestVisible = false };
        arrow.Fill = Brush(brush);
        GraphCanvas.Children.Add(arrow);
        if (Math.Abs(direction.X) > 0.1)
            return;

        var kinds = edges.Select(edge => KindLabel(edge.Relationship.Kind)).Distinct().ToArray();
        var label = new TextBlock
        {
            Text = kinds is [var single] ? single : $"{kinds.Length} kinds",
            FontSize = 11, Classes = { "muted" }, IsHitTestVisible = false
        };
        Canvas.SetLeft(label, start.X + (direction.Y > 0 ? 8 : -84));
        Canvas.SetTop(label, (start.Y + end.Y) / 2 - 7);
        GraphCanvas.Children.Add(label);
    }

    private static Point BoundaryOf(Rect bounds, Vector direction) => bounds.Center + direction
        * Math.Min(bounds.Width / 2 / Math.Abs(direction.X), bounds.Height / 2 / Math.Abs(direction.Y));

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    private static string ChangeBrush(CSharpChange change) => change switch
    {
        CSharpChange.Added => "FileAddedBrush",
        CSharpChange.Removed => "FileDeletedBrush",
        CSharpChange.Modified => "FileModifiedBrush",
        _ => "DiagramEdgeBrush"
    };

    private void ZoomIn(object? sender, RoutedEventArgs e) => SetZoom(_zoom + 0.2);
    private void ZoomOut(object? sender, RoutedEventArgs e) => SetZoom(_zoom - 0.2);
    private void ResetZoom(object? sender, RoutedEventArgs e) => SetZoom(1);

    private void SetZoom(double scale)
    {
        _zoom = Math.Clamp(scale, 0.4, 2);
        ZoomLayer.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        ZoomReset.Content = $"{Math.Round(_zoom * 100)}%";
    }

    private void PanStarted(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Viewport).Properties.IsMiddleButtonPressed) return;
        _panStart = e.GetPosition(Viewport);
        _panOffset = Viewport.Offset;
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void PanMoved(object? sender, PointerEventArgs e)
    {
        if (_panStart is not { } start) return;
        Viewport.Offset = _panOffset - (e.GetPosition(Viewport) - start);
        e.Handled = true;
    }

    private void PanEnded(object? sender, PointerReleasedEventArgs e)
    {
        if (_panStart is null) return;
        _panStart = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void PanLost(object? sender, PointerCaptureLostEventArgs e) => _panStart = null;
}
