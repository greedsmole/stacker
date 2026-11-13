using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Stacker.Desktop;

/// <summary>Keeps dialog actions visible while long forms and diagnostic details scroll.</summary>
internal static class DialogLayout
{
    /// <summary>Separates a heading and persistent action footer from a vertically scrollable form body.</summary>
    public static Grid Create(string title, string description, IEnumerable<Control> fields, params Control[] actions)
    {
        var heading = new StackPanel { Spacing = 6 };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
            heading.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        var body = new StackPanel { Spacing = 12 };
        foreach (var field in fields)
            body.Children.Add(field);
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var action in actions)
        {
            action.Margin = new Thickness(8, 4, 0, 0);
            footer.Children.Add(action);
        }
        var grid = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 16 };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(heading);
        grid.Children.Add(scroll);
        grid.Children.Add(footer);
        return grid;
    }
}
