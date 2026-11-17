using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Desktop.ViewModels;

namespace Stacker.Desktop;

public partial class ReviewPanel
{
    private void ClassDiagramClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (TopLevel.GetTopLevel(this) is Window { DataContext: MainViewModel main } owner
            && main.HasCSharpGraph && DataContext is ReviewViewModel review)
        {
            var section = review._section;
            var target = new DiagramTarget(section.Result.Layer, review.PullRequest.Number, section.IsPrSnapshot);
            await new ClassDiagramWindow(main, target).ShowDialog(owner);
        }
    }, message => AsyncHandler.Report(this, message));
}
