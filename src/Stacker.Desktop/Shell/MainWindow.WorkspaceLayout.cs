using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Stacker.Desktop.ViewModels;

namespace Stacker.Desktop;

public partial class MainWindow
{
    private const double DiffReadingWidth = 520;
    private const double MinPaneWidth = 200;
    private const double SplitterWidth = 4;
    private const double ReviewWidth = 360;
    private bool _focusMode;
    private bool _reviewCompact;
    private bool _reviewClosing;
    private bool _animateReviewOpen;
    private bool _layingOutWorkspace;
    private double _fittedStacksWidth;
    private double _fittedFilesWidth;
    private ReviewViewModel? _previousReview;
    private ReviewViewModel? _dismissedReview;
    private Control? _pressedMotionButton;
    private CancellationTokenSource? _reviewMotion;
    private int _reviewIntent;

    private void InitializeWorkspaceLayout()
    {
        Workspace.SizeChanged += (_, _) => ApplyWorkspaceLayout();
        Vm.PropertyChanged += WorkspacePropertyChanged;
        Closed += (_, _) =>
        {
            Vm.PropertyChanged -= WorkspacePropertyChanged;
            StopReviewMotion();
        };
        StacksSplitter.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            RememberStacksWidth();
            ApplyWorkspaceLayout();
        });
        FilesSplitter.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            RememberFilesWidth();
            ApplyWorkspaceLayout();
        });
        AddHandler(KeyDownEvent, (_, _) => _pressedMotionButton = null, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, MotionButtonPressed, RoutingStrategies.Tunnel);
        ApplyWorkspaceLayout();
        SetStackTab(Vm.IsLocalSelected);
    }

    private void WorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsLocalSelected) when Vm.SelectedStack is not null:
                SetStackTab(Vm.IsLocalSelected);
                break;
            case nameof(MainViewModel.ActiveReview):
            case nameof(MainViewModel.HasPrDiscussion):
            case nameof(MainViewModel.HasNoSections):
            case nameof(MainViewModel.AreFilesVisible):
            case nameof(MainViewModel.IsHistoryOpen):
            case nameof(MainViewModel.IsChangesOpen):
            case nameof(MainViewModel.IsOperationsOpen):
            case nameof(MainViewModel.Settings):
                ApplyWorkspaceLayout();
                break;
            case nameof(MainViewModel.IsReviewOpen):
                ApplyWorkspaceLayout(_animateReviewOpen);
                if (Vm.IsReviewOpen && _reviewCompact && ReviewHost.IsVisible)
                    CloseReviewButton.Focus();
                break;
        }
    }

    private void ApplyWorkspaceLayout(bool animateEntrance = false)
    {
        if (_layingOutWorkspace)
            return;
        _layingOutWorkspace = true;
        try
        {
            var width = Workspace.Bounds.Width > 0 ? Workspace.Bounds.Width : Width;
            var changes = Vm.IsChangesOpen && !Vm.IsHistoryOpen && !Vm.IsOperationsOpen;
            var showStacks = !_focusMode && Vm.IsChangesOpen && !Vm.Settings.StacksHidden;
            var showFiles = !_focusMode && changes && Vm.AreFilesVisible && !Vm.HasNoSections;
            var stacksWidth = showStacks ? Vm.Settings.NavigationWidth : 0;
            var filesWidth = showFiles ? Vm.Settings.FilesWidth : 0;
            var separators = (showStacks ? SplitterWidth : 0) + (showFiles ? SplitterWidth : 0);
            var overflow = Math.Max(0, stacksWidth + filesWidth + separators + DiffReadingWidth - width);
            var filesReduction = Math.Min(overflow, Math.Max(0, filesWidth - MinPaneWidth));
            filesWidth -= filesReduction;
            stacksWidth -= Math.Min(overflow - filesReduction, Math.Max(0, stacksWidth - MinPaneWidth));

            var review = Vm.ActiveReview;
            if (!ReferenceEquals(review, _previousReview))
            {
                _dismissedReview = null;
                _previousReview = review;
                StopReviewMotion();
            }
            var enoughRoom = width - stacksWidth - filesWidth - separators - ReviewWidth >= DiffReadingWidth;
            var canShowReview = changes && !_focusMode && Vm.CanPresentActiveReview;
            var showReview = canShowReview && (Vm.IsReviewOpen || _reviewClosing ||
                (enoughRoom && Vm.HasReviewablePrDiff && !ReferenceEquals(review, _dismissedReview)));
            var compact = showReview && !enoughRoom;
            var entering = showReview && !ReviewHost.IsVisible;
            if (!showReview || (ReviewHost.IsVisible && compact != _reviewCompact))
                _reviewMotion?.Cancel();
            _reviewCompact = compact;

            SetPane(Workspace, StacksPane, StacksSplitter, stacksWidth, showStacks);
            SetPane(WorkArea, SharedFiles, FilesSplitter, filesWidth, showFiles);
            _fittedStacksWidth = stacksWidth;
            _fittedFilesWidth = filesWidth;
            WorkArea.ColumnDefinitions[2].MinWidth = changes ? DiffReadingWidth : 0;
            Workspace.ColumnDefinitions[2].MinWidth = changes
                ? filesWidth + (showFiles ? SplitterWidth : 0) + DiffReadingWidth + (showReview && !compact ? ReviewWidth : 0)
                : 0;
            Grid.SetColumn(ReviewHost, compact ? 2 : 3);
            ReviewHost.Width = compact ? double.NaN : ReviewWidth;
            ReviewHost.IsVisible = showReview;
            DiffWorkspace.IsVisible = !compact && !Vm.IsHistoryOpen;
            CloseReviewButton.Content = compact ? "Back to diff" : "Close review";

            ChangesTab.IsChecked = changes;
            PaneControls.IsVisible = Vm.IsChangesOpen && !Vm.IsOperationsOpen;
            StacksToggle.IsChecked = showStacks;
            FilesToggle.IsChecked = showFiles;
            FilesToggle.IsEnabled = changes && !Vm.HasNoSections;
            FocusToggle.IsVisible = changes;
            FocusToggle.IsEnabled = !Vm.HasNoSections;
            FocusToggle.IsChecked = _focusMode;
            FocusToggle.Content = _focusMode ? "Exit focus" : "Focus";
            ToolTip.SetTip(FocusToggle, _focusMode ? "Restore navigation and review" : "Hide navigation and review for code reading");
            ToolTip.SetTip(StacksToggle, showStacks ? "Hide stacks and pull requests" : "Show stacks and pull requests");
            ToolTip.SetTip(FilesToggle, showFiles ? "Hide changed files" : "Show changed files");

            if (entering && animateEntrance && !Vm.Settings.ReduceMotion)
                AsyncHandler.Run(() => AnimateReviewAsync(entering: true), message => AsyncHandler.Report(this, message));
        }
        finally { _layingOutWorkspace = false; }
    }

    private static void SetPane(Grid parent, Control pane, GridSplitter splitter, double width, bool visible)
    {
        parent.ColumnDefinitions[0].MinWidth = visible ? MinPaneWidth : 0;
        parent.ColumnDefinitions[0].Width = new GridLength(width);
        parent.ColumnDefinitions[1].Width = new GridLength(visible ? SplitterWidth : 0);
        pane.IsVisible = splitter.IsVisible = visible;
    }

    private void RememberStacksWidth()
    {
        var width = Workspace.ColumnDefinitions[0].ActualWidth;
        if (StacksPane.IsVisible && width >= MinPaneWidth && Math.Abs(width - _fittedStacksWidth) > 1)
            Vm.Settings.NavigationWidth = Math.Clamp(width, MinPaneWidth, 500);
    }

    private void RememberFilesWidth()
    {
        var width = WorkArea.ColumnDefinitions[0].ActualWidth;
        if (SharedFiles.IsVisible && width >= MinPaneWidth && Math.Abs(width - _fittedFilesWidth) > 1)
            Vm.Settings.FilesWidth = Math.Clamp(width, MinPaneWidth, 600);
    }

    private void ToggleStacksClick(object? sender, RoutedEventArgs e)
    {
        RememberStacksWidth();
        if (_focusMode)
        {
            _focusMode = false;
            Vm.Settings.StacksHidden = false;
        }
        else
            Vm.Settings.StacksHidden = !Vm.Settings.StacksHidden;
        ApplyWorkspaceLayout();
    }

    private void ToggleFilesClick(object? sender, RoutedEventArgs e)
    {
        RememberFilesWidth();
        if (_focusMode)
        {
            _focusMode = false;
            Vm.AreFilesVisible = true;
        }
        else
            Vm.AreFilesVisible = !Vm.AreFilesVisible;
        ApplyWorkspaceLayout();
    }

    private void ToggleFocusClick(object? sender, RoutedEventArgs e)
    {
        RememberStacksWidth();
        RememberFilesWidth();
        _focusMode = !_focusMode;
        if (_focusMode)
            StopReviewMotion();
        ApplyWorkspaceLayout();
    }

    private void MotionButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedMotionButton = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is Visual visual
            ? visual as Button ?? visual.FindAncestorOfType<Button>() : null;
    }

    private bool TakePointerPress(object? sender)
    {
        var pointer = ReferenceEquals(_pressedMotionButton, sender) && sender is not null;
        _pressedMotionButton = null;
        return pointer;
    }

    private async Task OpenReviewFromHeaderAsync(Func<Task> open, Control? trigger, bool animate)
    {
        StopReviewMotion();
        _focusMode = false;
        _animateReviewOpen = animate;
        try
        {
            await open();
            ApplyWorkspaceLayout();
            if (_reviewCompact && ReviewHost.IsVisible)
                CloseReviewButton.Focus();
            else if (trigger is { IsEffectivelyVisible: true } && !animate)
                trigger.Focus();
        }
        finally { _animateReviewOpen = false; }
    }

    private void CloseReviewClick(object? sender, RoutedEventArgs e)
    {
        var animate = TakePointerPress(sender) && !Vm.Settings.ReduceMotion;
        AsyncHandler.Run(async () =>
        {
            var intent = ++_reviewIntent;
            _dismissedReview = Vm.ActiveReview;
            if (animate && ReviewHost.IsVisible && !_reviewClosing)
            {
                _reviewClosing = true;
                ReviewHost.IsHitTestVisible = false;
                Vm.CloseReviewCommand.Execute(null);
                await AnimateReviewAsync(entering: false);
                if (intent != _reviewIntent)
                    return;
            }
            StopReviewMotion();
            Vm.CloseReviewCommand.Execute(null);
            ApplyWorkspaceLayout();
            if (DiscussionButton.IsEffectivelyVisible)
                DiscussionButton.Focus();
            else
                FindButton.Focus();
        }, message => AsyncHandler.Report(this, message));
    }

    private async Task AnimateReviewAsync(bool entering)
    {
        _reviewMotion?.Cancel();
        using var motion = new CancellationTokenSource();
        _reviewMotion = motion;
        try
        {
            await WorkspaceMotion.RevealAsync(ReviewContent, TimeSpan.FromMilliseconds(entering ? 200 : 140),
                x: 10, entering: entering, cancellationToken: motion.Token);
        }
        catch (OperationCanceledException) when (motion.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_reviewMotion, motion))
                _reviewMotion = null;
        }
    }

    private void StopReviewMotion()
    {
        _reviewIntent++;
        _reviewMotion?.Cancel();
        _reviewClosing = false;
        ReviewHost.IsHitTestVisible = true;
    }
}
