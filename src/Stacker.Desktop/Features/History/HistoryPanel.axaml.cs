using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace Stacker.Desktop;

public sealed partial class HistoryPanel : UserControl
{
    public HistoryPanel() => AvaloniaXamlLoader.Load(this);
}
