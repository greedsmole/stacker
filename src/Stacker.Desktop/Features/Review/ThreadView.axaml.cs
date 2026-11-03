// Purpose: Presentation adapter for an individual PR review thread, including reply, resolve, and navigation actions.
using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;
public partial class ThreadView : UserControl
{
    public static readonly StyledProperty<string?> OriginalCodeProperty = AvaloniaProperty.Register<ThreadView, string?>(nameof(OriginalCode));
    public string? OriginalCode { get => GetValue(OriginalCodeProperty); private set => SetValue(OriginalCodeProperty, value); }
    public ThreadView()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateOriginalCode();
        DataContextChanged += (_, _) => UpdateOriginalCode();
    }
    private void UpdateOriginalCode()
    {
        OriginalCode = null;
        if (DataContext is not ReviewThread { IsOutdated: false, Anchor: { Side: "RIGHT" } anchor } || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        var section = main.Sections.FirstOrDefault(s => s.IsPrSnapshot && s.AssociatedPr?.Number == anchor.PullRequest && s.Result.HeadSha == anchor.HeadSha && s.SelectedFile?.NewPath == anchor.Path);
        if (section is null) return;
        var start = anchor.StartLine ?? anchor.Line;
        var lines = section.Lines.Where(l => l.Kind is DiffLineKind.Added or DiffLineKind.Context && l.NewLineNumber >= start && l.NewLineNumber <= anchor.Line).OrderBy(l => l.NewLineNumber).ToArray();
        if (lines.Length == anchor.Line - start + 1) OriginalCode = string.Join("\n", lines.Select(l => l.Text.TrimEnd('\r')));
    }
    private ReviewViewModel? Session => this.GetVisualAncestors().OfType<ReviewPanel>().FirstOrDefault()?.DataContext as ReviewViewModel
        ?? (this.GetVisualAncestors().OfType<DiffSectionView>().FirstOrDefault()?.DataContext as DiffSectionViewModel)?.Review;
    private void ReplyClick(object? sender, RoutedEventArgs e)
=>AsyncHandler.Run(async () => {         if (DataContext is not ReviewThread thread || Session is not { } session || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        session.BeginReply(thread); main.ActiveReview = session; main.IsReviewOpen = true;
        try { await session.FlushAsync(); }
        catch (Exception ex) { main.Error = ex.Message; }
 }, message => AsyncHandler.Report(this, message));
    private void ResolveClick(object? sender, RoutedEventArgs e)
=>AsyncHandler.Run(async () => { if (DataContext is ReviewThread thread && Session is { } session) { session.SelectedThread = thread; await session.ToggleResolvedCommand.ExecuteAsync(null); }  }, message => AsyncHandler.Report(this, message));
    private void GoToClick(object? sender, RoutedEventArgs e)
=>AsyncHandler.Run(async () => {         if (DataContext is not ReviewThread thread || thread.IsOutdated || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        var section = main.Sections.FirstOrDefault(s => s.IsPrSnapshot && s.AssociatedPr?.Number == Session?.PullRequest.Number);
        if (section is null) return;
        section.Filter = ""; section.SelectedFile = section.Files.FirstOrDefault(f => f.NewPath == thread.Path);
        await section.LoadAsync(section.SelectedFile);
        if (TopLevel.GetTopLevel(this) is MainWindow window)
            window.GetVisualDescendants().OfType<DiffSectionView>().FirstOrDefault(v => ReferenceEquals(v.DataContext, section))?.ScrollTo(thread.Anchor);
 }, message => AsyncHandler.Report(this, message));
}
