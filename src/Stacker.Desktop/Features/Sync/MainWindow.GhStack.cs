using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Desktop;

public partial class MainWindow
{
    private void GhStackBackendClick(object? sender, RoutedEventArgs e) =>
        AsyncHandler.Run(async () =>
        {
            await Vm.ChooseStackBackendAsync(Vm.UsesGhStack ? StackBackend.Stacker : StackBackend.GitHubCli);
            GhStackBackendToggle.IsChecked = Vm.UsesGhStack;
        }, message => AsyncHandler.Report(this, message));

    private void LoadGhStackClick(object? sender, RoutedEventArgs e) =>
        AsyncHandler.Run(Vm.LoadGhStackAsync, message => AsyncHandler.Report(this, message));

    private void CreateGhStackClick(object? sender, RoutedEventArgs e) =>
        AsyncHandler.Run(() => ShowGhStackBranchDialogAsync(create: true), message => AsyncHandler.Report(this, message));

    private void AddGhLayerClick(object? sender, RoutedEventArgs e) =>
        AsyncHandler.Run(() => ShowGhStackBranchDialogAsync(create: false), message => AsyncHandler.Report(this, message));

    private async Task ShowGhStackBranchDialogAsync(bool create)
    {
        if (!(create ? Vm.CanCreateGhStack : Vm.CanAddGhLayer) || Vm.Repository is not { } repository)
            return;

        var title = create ? "Create GitHub CLI stack" : "Add GitHub CLI layer";
        var branch = new TextBox { Watermark = "New branch name" };
        Avalonia.Automation.AutomationProperties.SetName(branch, "New branch name");
        var branches = repository.Refs.Where(reference => reference.Name.StartsWith(LocalBranch.Prefix, StringComparison.Ordinal))
            .Select(reference => StackRefs.Short(reference.Name)).ToArray();
        var trunk = new ComboBox
        {
            ItemsSource = branches,
            SelectedItem = branches.FirstOrDefault(name => name == Vm.CheckedOutBranch) ?? branches.FirstOrDefault(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Avalonia.Automation.AutomationProperties.SetName(trunk, "Trunk branch");
        var error = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        error.Bind(SelectableTextBlock.ForegroundProperty, error.GetResourceObservable("WarningBrush"));
        var fields = new List<Control>();
        if (create)
            fields.Add(DialogLayout.Field("Trunk branch", trunk));
        fields.Add(DialogLayout.Field("New branch", branch));
        fields.Add(error);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var submit = new Button { Content = create ? "Create stack" : "Add layer", IsDefault = true, Classes = { "primary" } };
        var window = new Window
        {
            Title = title, Width = 480, Height = 315, MinWidth = 410, MinHeight = 270,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = DialogLayout.Create(title,
                create ? "Create and check out the first layer." : $"Create a layer on {Vm.CheckedOutBranch}.",
                fields, cancel, submit)
        };
        cancel.Click += (_, _) => window.Close();
        submit.Click += (_, _) => AsyncHandler.Run(async () =>
        {
            var name = branch.Text?.Trim() ?? "";
            if (name.Length == 0 || create && trunk.SelectedItem is null)
            {
                error.Text = "Choose the trunk and enter a branch name.";
                error.IsVisible = true;
                return;
            }
            submit.IsEnabled = cancel.IsEnabled = false;
            try
            {
                var result = create ? await Vm.CreateGhStackAsync((string)trunk.SelectedItem!, name) : await Vm.AddGhLayerAsync(name);
                if (result.Ok || result.NeedsUser)
                {
                    window.Close();
                    return;
                }
                error.Text = result.Message;
                error.IsVisible = true;
            }
            finally { submit.IsEnabled = cancel.IsEnabled = true; }
        }, message => AsyncHandler.Report(this, message));
        window.Opened += (_, _) => branch.Focus();
        await window.ShowDialog(this);
    }

    private void PreviewGhStackClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        var plan = (await Vm.PreviewGhStackAsync()).OrThrow();
        var affected = new TextBlock
        {
            Text = string.Join("\n", plan.UpperLayers.Select(reference => $"{StackRefs.Short(reference.Name)}  ·  {reference.Sha[..8]}")),
            Classes = { "code-meta" }, TextWrapping = TextWrapping.Wrap
        };
        var confirmed = await ConfirmGhStackAsync("Rebase upper layers", "Review the branches GitHub CLI can rewrite in this worktree.",
            "Rebase locally", [affected]);
        if (confirmed)
            ReportGhStack(await Vm.RebaseGhStackAsync(plan));
    }, message => AsyncHandler.Report(this, message));

    private void ContinueGhStackClick(object? sender, RoutedEventArgs e) =>
        AsyncHandler.Run(async () => ReportGhStack(await Vm.RecoverGhStackAsync(abort: false)), message => AsyncHandler.Report(this, message));

    private void AbortGhStackClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (await ConfirmGhStackAsync("Abort GitHub CLI rebase", "Restore the original branch tips and discard the current conflict resolution.",
            "Abort rebase", []))
            ReportGhStack(await Vm.RecoverGhStackAsync(abort: true));
    }, message => AsyncHandler.Report(this, message));

    private void AcknowledgeGhStackClick(object? sender, RoutedEventArgs e) =>
        AsyncHandler.Run(async () => ReportGhStack(await Vm.AcknowledgeGhStackAsync()), message => AsyncHandler.Report(this, message));

    private void ReportGhStack(CommandOutcome result)
    {
        if (!result.Ok && !result.NeedsUser)
            Vm.Error = result.Message;
    }

    private Task<bool> ConfirmGhStackAsync(string title, string message, string action, Control[] fields)
    {
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var confirm = new Button { Content = action, Classes = { "primary" } };
        var window = new Window
        {
            Title = title, Width = 500, Height = 340, MinWidth = 430, MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = DialogLayout.Create(title, message, fields, cancel, confirm)
        };
        cancel.Click += (_, _) => window.Close(false);
        confirm.Click += (_, _) => window.Close(true);
        return window.ShowDialog<bool>(this);
    }
}
