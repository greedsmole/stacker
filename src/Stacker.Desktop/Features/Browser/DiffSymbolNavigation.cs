using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Stacker.Core;
using Stacker.Desktop.ViewModels;

namespace Stacker.Desktop;

public partial class DiffSectionView
{
    private CancellationTokenSource? _symbolLoad;

    private void CodeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not SyntaxText text || !TryGetSymbolContext(text, out var section, out var file, out var main, out var window)) return;
        var column = Math.Min(text.SelectionStart, text.SelectionEnd);
        if (e.TryGetPosition(text, out var point))
        {
            var hit = text.TextLayout.HitTestPoint(point);
            if (!hit.IsInside) return;
            column = hit.TextPosition;
        }

        var definition = new MenuItem { Header = "Go to Definition", InputGesture = new(Key.F12) };
        var references = new MenuItem { Header = "Find References", InputGesture = new(Key.F12, KeyModifiers.Shift) };
        var row = text.Line!;
        definition.Click += (_, _) => StartSymbolNavigation(section, file, row, main, window, column, SymbolNavigationKind.Definition);
        references.Click += (_, _) => StartSymbolNavigation(section, file, row, main, window, column, SymbolNavigationKind.References);
        var menu = new ContextMenu { ItemsSource = new[] { definition, references } };
        menu.Open(text);
        e.Handled = true;
    }

    private void CodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12 || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift) || sender is not SyntaxText text ||
            !TryGetSymbolContext(text, out var section, out var file, out var main, out var window)) return;
        e.Handled = true;
        StartSymbolNavigation(section, file, text.Line!, main, window, Math.Min(text.SelectionStart, text.SelectionEnd),
            e.KeyModifiers == KeyModifiers.Shift ? SymbolNavigationKind.References : SymbolNavigationKind.Definition);
    }

    private bool TryGetSymbolContext(SyntaxText text, out DiffSectionViewModel section, out FileChange file, out MainViewModel main, out Window window)
    {
        section = (DataContext as DiffSectionViewModel)!;
        file = section?.SelectedFile!;
        window = (TopLevel.GetTopLevel(this) as Window)!;
        main = (window?.DataContext as MainViewModel)!;
        if (text.Line is not { IsCode: true } line || file is null || main?.SymbolNavigation is null) return false;
        var path = line.IsRemoved ? file.OldPath : file.NewPath;
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private void StartSymbolNavigation(DiffSectionViewModel section, FileChange file, RenderedDiffLine row, MainViewModel main,
        Window window, int column, SymbolNavigationKind kind) =>
        AsyncHandler.Run(async () =>
        {
            var line = row.IsRemoved ? row.OldLineNumber : row.NewLineNumber;
            if (line is null || section.SelectedFile != file || !section.Lines.Contains(row)) return;
            var request = new SymbolNavigationRequest(section.Root, row.IsRemoved ? section.Result.BaseSha : section.Result.HeadSha,
                row.IsRemoved ? file.OldPath : file.NewPath, line.Value, column, kind);
            if (_symbolLoad is not null) return;
            using var load = new CancellationTokenSource();
            _symbolLoad = load;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var work = CancellationTokenSource.CreateLinkedTokenSource(load.Token, deadline.Token);
            const string working = "Loading C# project and resolving symbol…";
            var previousMessage = section.Message;
            section.Message = working;
            try
            {
                var projects = (await main.SymbolNavigation!.ProjectsAsync(request, work.Token)).OrThrow();
                string? project;
                if (projects.Count == 1) project = projects[0];
                else
                {
                    deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    project = await ChooseProjectAsync(window, projects, load.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(45));
                }
                if (project is null) return;
                var result = (await main.SymbolNavigation.FindAsync(request, project, work.Token)).OrThrow();
                work.Token.ThrowIfCancellationRequested();
                if (section.SelectedFile != file || DataContext != section || !IsLoaded) return;
                if (result.Locations.Count == 0)
                {
                    section.Message = kind == SymbolNavigationKind.Definition ? "No declaration in the checked-in C# source."
                        : "No references in the loaded C# solution or project.";
                    return;
                }
                section.Message = previousMessage;
                await ShowSymbolLocationsAsync(window, main, request, result, load.Token);
            }
            catch (OperationCanceledException) when (load.IsCancellationRequested) { }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            { section.Message = "C# navigation timed out while loading the project or finding the symbol."; }
            finally
            {
                if (section.Message == working) section.Message = previousMessage;
                _symbolLoad = null;
            }
        }, message => AsyncHandler.Report(this, message));

    private static async Task<string?> ChooseProjectAsync(Window owner, IReadOnlyList<string> projects, CancellationToken ct)
    {
        var list = new ListBox { ItemsSource = projects, SelectedIndex = 0, MaxHeight = 280 };
        var open = new Button { Content = "Load project", IsDefault = true, Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window { Title = "C# project", Width = 660, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = DialogLayout.Create("Choose a C# project", "Select the solution Stacker will load with MSBuild.", [list], cancel, open);
        open.Click += (_, _) => dialog.Close(list.SelectedItem as string);
        cancel.Click += (_, _) => dialog.Close();
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
        return await dialog.ShowDialog<string?>(owner);
    }

    private static async Task ShowSymbolLocationsAsync(Window owner, MainViewModel main, SymbolNavigationRequest request,
        SymbolNavigationResult result, CancellationToken ct)
    {
        var definition = request.Kind == SymbolNavigationKind.Definition;
        var showList = !definition || result.Locations.Count > 1;
        var dialog = new Window
        {
            Title = definition ? "Definition" : "References",
            Width = 930, Height = 610, MinWidth = 650, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var label = new TextBlock { Text = result.Symbol, FontSize = 18, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12, Children = { label, close } };
        Grid.SetColumn(close, 1);

        var locations = new ListBox
        {
            ItemsSource = result.Locations, IsVisible = showList,
            ItemTemplate = new FuncDataTemplate<SymbolLocation>((location, _) =>
            {
                var caption = $"{location.Path}:{location.Line}";
                var text = new TextBlock { Text = caption, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(text, caption);
                return text;
            })
        };
        var source = new ListBox
        {
            FontFamily = new("Menlo,Consolas,monospace"),
            ItemTemplate = new FuncDataTemplate<string>((text, _) => new SelectableTextBlock { Text = text })
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(source, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var body = new Grid { ColumnDefinitions = new(showList ? "280,*" : "0,*"), ColumnSpacing = showList ? 12 : 0,
            Children = { locations, source } };
        Grid.SetColumn(source, 1);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        var layout = new Grid { Margin = new Thickness(16), RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8, Children = { header, status, body } };
        Grid.SetRow(status, 1);
        Grid.SetRow(body, 2);
        dialog.Content = layout;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
        dialog.Closed += (_, _) => lifetime.Cancel();
        var generation = 0;
        locations.SelectionChanged += (_, _) =>
        {
            if (locations.SelectedItem is not SymbolLocation target) return;
            var selection = ++generation;
            var caption = $"{target.Path}:{target.Line}";
            status.Text = "Loading " + caption + "…";
            source.ItemsSource = null;
            AsyncHandler.Run(async () =>
            {
                try
                {
                    var blob = await main.ReadSymbolSourceAsync(request, target, lifetime.Token);
                    if (lifetime.IsCancellationRequested || selection != generation) return;
                    if (blob is null) { status.Text = "Source unavailable for " + caption; return; }
                    var lines = blob.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                    source.ItemsSource = lines.Select((text, index) => $"{index + 1,5}  {text}").ToArray();
                    status.Text = caption + (result.Truncated ? " · First 500 results shown" : "");
                    source.SelectedIndex = target.Line - 1;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!lifetime.IsCancellationRequested && selection == generation && source.SelectedIndex >= 0)
                            source.ScrollIntoView(source.SelectedIndex);
                    }, DispatcherPriority.Loaded);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }, message => { if (selection == generation && !lifetime.IsCancellationRequested) status.Text = message; });
        };
        dialog.Opened += (_, _) => locations.SelectedIndex = 0;
        ct.ThrowIfCancellationRequested();
        await dialog.ShowDialog(owner);
    }
}
