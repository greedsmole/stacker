using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;
public partial class ThreadView : UserControl
{
    public ThreadView() => InitializeComponent();
    private ReviewViewModel? Session => this.GetVisualAncestors().OfType<ReviewPanel>().FirstOrDefault()?.DataContext as ReviewViewModel
        ?? (this.GetVisualAncestors().OfType<DiffSectionView>().FirstOrDefault()?.DataContext as DiffSectionViewModel)?.Review;
    private async void ReplyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReviewThread thread || Session is not { } session || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        session.BeginReply(thread); main.ActiveReview = session; main.IsReviewOpen = true; await session.FlushAsync();
    }
    private async void ResolveClick(object? sender, RoutedEventArgs e)
    { if (DataContext is ReviewThread thread && Session is { } session) { session.SelectedThread = thread; await session.ToggleResolvedCommand.ExecuteAsync(null); } }
    private async void GoToClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReviewThread thread || thread.IsOutdated || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        var section = main.Sections.FirstOrDefault(s => s.IsPrSnapshot && s.AssociatedPr?.Number == Session?.PullRequest.Number);
        if (section is null) return;
        section.Filter = ""; section.SelectedFile = section.Files.FirstOrDefault(f => f.NewPath == thread.Path);
        await section.LoadAsync(section.SelectedFile);
        if (TopLevel.GetTopLevel(this) is MainWindow window)
            window.GetVisualDescendants().OfType<DiffSectionView>().FirstOrDefault(v => ReferenceEquals(v.DataContext, section))?.ScrollTo(thread.Anchor);
    }
}
