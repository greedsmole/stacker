using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Stacker.Desktop;

/// <summary>Keeps dialog actions visible while long forms and diagnostic details scroll.</summary>
internal static class DialogLayout
{
    /// <summary>Separates a heading and persistent action footer from a vertically scrollable form body.</summary>
    public static Grid Create(string title, string description, IEnumerable<Control> fields, params Control[] actions)
    {
        var heading = Stack("StackerSpace4");
        heading.Children.Add(new TextBlock { Text = title, Classes = { "view-title" }, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
            heading.Children.Add(Caption(description));
        var header = Surface(heading, "dialog-heading");
        var body = Stack("StackerSpace24");
        body.Margin = new Thickness(24);
        var controls = fields.ToArray();
        for (var index = 0; index < controls.Length; index++)
        {
            var field = controls[index];
            if (field is TextBlock label && !label.Classes.Contains("pane-title")
                && index + 1 < controls.Length && IsInput(controls[index + 1]))
            {
                var group = Stack("StackerSpace4");
                label.Classes.Add("section-title");
                label.TextWrapping = TextWrapping.Wrap;
                group.Children.Add(label);
                group.Children.Add(controls[++index]);
                if (index + 1 < controls.Length && controls[index + 1] is TextBlock help
                    && !help.Classes.Contains("pane-title")
                    && (index + 2 >= controls.Length || !IsInput(controls[index + 2])))
                {
                    help.Classes.Add("muted");
                    help.Bind(TextBlock.FontSizeProperty, help.GetResourceObservable("StackerCaptionFontSize"));
                    help.TextWrapping = TextWrapping.Wrap;
                    group.Children.Add(help);
                    index++;
                }
                body.Children.Add(group);
            }
            else
                body.Children.Add(field);
        }
        var scroll = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var actionList = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var action in actions)
        {
            if (action is Button button && button.IsCancel && !button.Classes.Contains("primary"))
                button.Classes.Add("quiet");
            actionList.Children.Add(action);
        }
        var footer = Surface(actionList, "dialog-footer");
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(scroll);
        grid.Children.Add(footer);
        return grid;
    }

    public static StackPanel Field(string label, Control input, string? help = null)
    {
        var field = Stack("StackerSpace4");
        field.Children.Add(new TextBlock { Text = label, Classes = { "section-title" }, TextWrapping = TextWrapping.Wrap });
        field.Children.Add(input);
        if (!string.IsNullOrEmpty(help))
        {
            field.Children.Add(Caption(help));
            Avalonia.Automation.AutomationProperties.SetHelpText(input, help);
        }
        return field;
    }

    public static StackPanel Section(string title, params Control[] fields)
    {
        var section = Stack("StackerSpace16");
        section.Children.Add(new TextBlock { Text = title, Classes = { "pane-title" } });
        foreach (var field in fields)
            section.Children.Add(field);
        return section;
    }

    private static bool IsInput(Control control) => control is TextBox or ComboBox or NumericUpDown;

    private static StackPanel Stack(string spacingToken)
    {
        var panel = new StackPanel();
        panel.Bind(StackPanel.SpacingProperty, panel.GetResourceObservable(spacingToken));
        return panel;
    }

    private static TextBlock Caption(string text)
    {
        var caption = new TextBlock { Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        caption.Bind(TextBlock.FontSizeProperty, caption.GetResourceObservable("StackerCaptionFontSize"));
        return caption;
    }

    private static Border Surface(Control content, string appearance) =>
        new() { Child = content, Padding = new Thickness(24, 16), Classes = { appearance } };
}
