// Purpose: Names and starts one new stack layer; the branch is created here and committed by the user.
using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Core;
namespace Stacker.Desktop;

public partial class AddLayerWindow : Window
{
    private readonly StackDefinition? _stack;
    private readonly Func<string, string, string, Task<(bool Ok, string Message)>>? _create;
    public AddLayerWindow()
    {
        InitializeComponent();
    }
    public AddLayerWindow(StackDefinition stack, Func<string, string, string, Task<(bool Ok, string Message)>> create) : this()
    {
        _stack = stack;
        _create = create;
        StackLabel.Text = $"Stack · {stack.Name}";
        var starts = new List<LayerStart> { new("Current checkout (HEAD)", "HEAD") };
        if (stack.Base.Length > 0)
            starts.Add(new($"Base · {StackRefs.Short(stack.Base)}", stack.Base));
        for (var position = 0; position < stack.Branches.Count; position++)
            starts.Add(new($"Layer {position + 1} · {StackRefs.Short(stack.Branches[position])}", stack.Branches[position]));
        StartInput.ItemsSource = starts;
        StartInput.SelectedItem = stack.Branches.Count > 0 ? starts[^1] : starts[0];
        NameInput.Focus();
    }
    private void CreateClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (_stack is null || _create is null)
        return;
    if (StartInput.SelectedItem is not LayerStart start)
    {
        ErrorLabel.Text = "Choose a start point.";
        return;
    }
    var name = NameInput.Text?.Trim() ?? "";
    if (name.Length == 0)
    {
        ErrorLabel.Text = "Enter a branch name for the new layer.";
        return;
    }
    IsEnabled = false;
    ErrorLabel.Text = "";
    try
    {
        var (ok, message) = await _create(_stack.Id, name, start.Reference);
        if (ok)
            Close(true);
        else
            ErrorLabel.Text = message;
    }
    catch (Exception ex) { ErrorLabel.Text = ex.Message; }
    finally { IsEnabled = true; }
}, message => AsyncHandler.Report(this, message));
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
