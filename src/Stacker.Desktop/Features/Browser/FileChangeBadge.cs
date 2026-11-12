using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Stacker.Core;

namespace Stacker.Desktop;

public sealed class FileChangeBadge : Border
{
    public static readonly StyledProperty<FileChange?> ChangeProperty =
        AvaloniaProperty.Register<FileChangeBadge, FileChange?>(nameof(Change));

    private readonly TextBlock _label = new()
    {
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    public FileChangeBadge()
    {
        Width = 19;
        Height = 18;
        VerticalAlignment = VerticalAlignment.Center;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(3);
        Background = Brushes.Transparent;
        IsVisible = false;
        Child = _label;
        _label.Bind(TextBlock.ForegroundProperty, this.GetObservable(BorderBrushProperty));
    }

    public FileChange? Change
    {
        get => GetValue(ChangeProperty);
        set => SetValue(ChangeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != ChangeProperty) return;

        IsVisible = Change is not null;
        if (Change is not { } file) return;
        _label.Text = file.Kind switch
        {
            FileChangeKind.Added => "A",
            FileChangeKind.Deleted => "D",
            FileChangeKind.Renamed => "R",
            FileChangeKind.Copied => "C",
            FileChangeKind.TypeChanged => "T",
            _ => "M"
        };
        Classes.Set("added", file.Kind == FileChangeKind.Added);
        Classes.Set("deleted", file.Kind == FileChangeKind.Deleted);
        Classes.Set("relocated", file.Kind is FileChangeKind.Renamed or FileChangeKind.Copied);
        Classes.Set("type-changed", file.Kind == FileChangeKind.TypeChanged);
        var detail = file.IsSubmodule || file.ModeChanged ? $" · {file.Detail}" : "";
        AutomationProperties.SetName(this, file.Kind.ToString());
        ToolTip.SetTip(this, $"{file.Kind}{detail} · {file.Stats}\n{file.DisplayPath}");
    }
}
