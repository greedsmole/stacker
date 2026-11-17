using Avalonia.Interactivity;

namespace Stacker.Desktop;

public partial class MainWindow
{
    private void ClassDiagramClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (Vm.HasCSharpGraph && Vm.Sections.LastOrDefault() is { } section)
        {
            var target = new DiagramTarget(section.Result.Layer, section.IsPrSnapshot ? section.AssociatedPr?.Number : null, true);
            await new ClassDiagramWindow(Vm, target).ShowDialog(this);
        }
    }, message => AsyncHandler.Report(this, message));
}
