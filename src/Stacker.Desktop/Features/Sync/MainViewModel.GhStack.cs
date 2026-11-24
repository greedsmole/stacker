using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IGhStackClient? _ghStacks;
    private GhStackTool? _ghStackTool;
    private GhStackSnapshot? _ghStackSnapshot;
    private CancellationTokenSource? _ghStackLoad;

    [ObservableProperty] private StackGroupViewModel? _ghStackGroup;
    [ObservableProperty] private GhStackRecovery? _ghStackRecovery;
    [ObservableProperty] private bool _isGhStackBusy;
    [ObservableProperty] private string _ghStackStatus = "";

    public bool HasGhStackBackend => _ghStacks is not null && CanEdit && !IsDemo;
    public bool UsesGhStack => _runtime.StackBackend == StackBackend.GitHubCli;
    public bool CanChooseStackBackend => HasGhStackBackend && !IsGhStackBusy && !IsSyncing && !HasOperation
        && GhStackRecovery is null && _runtime._activeEntry is not { Kind: "restack", NeedsAttention: true };
    public bool CanManageStackerStacks => !UsesGhStack && GhStackRecovery is null;
    public bool CanEditStackerSelection => CanManageStackerStacks && _runtime._activeGroup is { IsRemote: false, UsesGhStack: false } && CanEdit;
    public bool CanLoadGhStack => HasGhStackBackend && UsesGhStack && !IsGhStackBusy && !IsSyncing && GhStackRecovery is null;
    private bool CanRunGhStack => CanLoadGhStack && !HasOperation && _ghStackTool?.CanManage == true;
    public bool CanCreateGhStack => CanRunGhStack;
    public bool CanAddGhLayer => CanRunGhStack && _ghStackSnapshot?.Branches.LastOrDefault(item => !item.IsMerged)?.Name == CheckedOutBranch
        && CheckedOutBranch is not null;
    public bool CanRebaseGhStack => CanRunGhStack && _ghStackSnapshot is { } stack && stack.CurrentBranch == CheckedOutBranch
        && stack.Branches.Skip(1).Any(item => !item.IsMerged);
    public bool HasGhStackRecovery => GhStackRecovery is not null;
    public bool CanContinueGhStack => !IsSyncing && GhStackRecovery?.CanContinue == true;
    public bool CanAbortGhStack => !IsSyncing && GhStackRecovery?.CanAbort == true;
    public bool CanAcknowledgeGhStack => !IsSyncing && GhStackRecovery?.CanAcknowledge == true;

    private static string BackendKey(RepositorySnapshot repository) => repository.Identity?.Repository.Value ?? repository.Root;

    public async Task LoadRepositoryAuthoringAsync(RepositorySnapshot repository, CancellationToken ct)
    {
        CloseWorkingTree();
        _ghStackLoad?.Cancel();
        _ghStackTool = null;
        _ghStackSnapshot = null;
        GhStackGroup = null;
        GhStackRecovery = null;
        IsGhStackBusy = false;
        _runtime.StackBackend = StackBackend.Stacker;
        GhStackStatus = "Load the checked-out GitHub CLI stack.";
        try
        {
            var saved = (await _runtime._applicationStore.ReadAsync<StackBackendPreference>("stack-backends", BackendKey(repository), ct)).OrThrow();
            if (ct.IsCancellationRequested || Repository?.Root != repository.Root)
                return;
            if (_ghStacks is not null && !repository.IsDemo && saved?.Backend == StackBackend.GitHubCli)
                _runtime.StackBackend = StackBackend.GitHubCli;
            await RefreshGhStackRecoveryAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Error = ex.Message; }
        finally { NotifyGhStackActions(); }
    }

    public async Task ChooseStackBackendAsync(StackBackend backend)
    {
        if (!CanChooseStackBackend || _runtime.StackBackend == backend || Repository is not { } repository)
            return;
        IsGhStackBusy = true;
        try
        {
            (await _runtime._applicationStore.WriteAsync("stack-backends", BackendKey(repository), new StackBackendPreference(backend))).OrThrow();
            if (Repository?.Root != repository.Root)
                return;
            _runtime.StackBackend = backend;
            if (!UsesGhStack)
                await ReplaceGhGroupAsync(null, inspect: false);
            else
                GhStackStatus = "Load the checked-out GitHub CLI stack.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsGhStackBusy = false;
            NotifyGhStackActions();
        }
        if (UsesGhStack)
            await LoadGhStackAsync();
    }

    public async Task LoadGhStackAsync()
    {
        if (CanLoadGhStack)
            await ReadGhStackAsync(inspect: true, refreshRepository: true);
    }

    private async Task ReadGhStackAsync(bool inspect, bool refreshRepository = false)
    {
        if (_ghStacks is null || Repository is not { } repository)
            return;
        _ghStackLoad?.Cancel();
        var load = new CancellationTokenSource();
        _ghStackLoad = load;
        IsGhStackBusy = true;
        GhStackStatus = "Loading GitHub CLI stack…";
        try
        {
            if (refreshRepository)
                await ReloadLocalAsync();
            if (load.IsCancellationRequested || Repository is not { } current || current.Root != repository.Root)
                return;
            repository = current;
            var target = StackTarget(repository);
            var probe = (await _ghStacks.ProbeAsync(target, load.Token)).OrThrow();
            var view = (await _ghStacks.LoadAsync(target, load.Token)).OrThrow();
            if (load.IsCancellationRequested || Repository?.Root != repository.Root)
                return;
            _ghStackTool = probe;
            _ghStackSnapshot = view.Stack;
            var branches = view.Stack?.Branches.Where(branch => !branch.IsMerged).ToArray() ?? [];
            var group = view.Stack is { } stack && branches.Length > 0
                ? await MakeGhStackGroup(repository, stack, branches, load.Token) : null;
            if (load.IsCancellationRequested || Repository?.Root != repository.Root)
                return;
            GhStackStatus = group is null ? view.Message : $"GitHub CLI · {branches.Length} active {(branches.Length == 1 ? "layer" : "layers")}";
            var queued = branches.Count(layer => layer.IsQueued);
            var behind = branches.Count(layer => layer.NeedsRebase);
            if (queued > 0)
                GhStackStatus += $" · {queued} queued";
            if (behind > 0)
                GhStackStatus += $" · {behind} to rebase";
            if (string.IsNullOrEmpty(GhStackStatus))
                GhStackStatus = "All layers have merged.";
            if (!probe.CanManage)
                GhStackStatus += $" CLI {probe.Version} can inspect. Install 0.3.x to manage stacks here.";
            await ReplaceGhGroupAsync(group, inspect);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!load.IsCancellationRequested && Repository?.Root == repository.Root)
            {
                _ghStackTool = null;
                _ghStackSnapshot = null;
                GhStackStatus = ex.Message;
                await ReplaceGhGroupAsync(null, inspect: false);
            }
        }
        finally
        {
            if (ReferenceEquals(_ghStackLoad, load))
            {
                _ghStackLoad = null;
                IsGhStackBusy = false;
            }
            load.Dispose();
            NotifyGhStackActions();
        }
    }

    private async Task<StackGroupViewModel> MakeGhStackGroup(RepositorySnapshot repository, GhStackSnapshot stack,
        IReadOnlyList<GhStackBranch> branches, CancellationToken ct)
    {
        var definition = new StackDefinition("gh-stack-" + StableIdentity.For(stack.Trunk, stack.Branches[0].Name)[..16],
            branches[^1].Name, LocalBranch.Prefix + stack.Trunk, branches.Select(branch => LocalBranch.Prefix + branch.Name).ToArray());
        var snapshots = await _runtime._stacks.SnapshotAsync(repository, definition, ct);
        var group = new StackGroupViewModel(definition, snapshots) { UsesGhStack = true, IsExpanded = true };
        for (var i = 0; i < group.Layers.Count; i++)
            if (FindLocalPr(group.Layers[i].Snapshot.Branch) is { } pr)
                group.Layers[i] = new(group.Layers[i].Snapshot, pr) { Group = group };
        return group;
    }

    private async Task ReplaceGhGroupAsync(StackGroupViewModel? group, bool inspect)
    {
        var old = GhStackGroup;
        var wasActive = old is not null && ReferenceEquals(_runtime._activeGroup, old);
        var selectedRef = wasActive ? old!.Layers.FirstOrDefault(layer => layer.IsSelected)?.Snapshot.Branch : null;
        GhStackGroup = group;
        if (group is not null && (wasActive || inspect))
        {
            var layer = group.Layers.FirstOrDefault(item => item.Snapshot.Branch == (selectedRef ?? LocalBranch.Prefix + CheckedOutBranch));
            if (layer is not null)
                await SelectLayerAsync(layer, false);
            else
                await SelectGroupAsync(group);
        }
        else if (group is null && wasActive)
        {
            await SaveWorkspaceAsync();
            _runtime._comparison?.Cancel();
            _runtime._activeGroup = null;
            _runtime._selectedPositions = [];
            _runtime._suppress = true;
            SelectedStack = null;
            _runtime._suppress = false;
            Layers.Clear();
            Sections.Clear();
            ActiveReview = null;
            IsReviewOpen = false;
            CloseHistoryCommand.Execute(null);
            EmptyMessage = "Select a stack to inspect its changes.";
            OnPropertyChanged(nameof(IsLocalSelected));
        }
    }

    public Task<CommandOutcome> CreateGhStackAsync(string trunk, string branch) => CanCreateGhStack
        ? RunGhStackAsync((client, target) => client.CreateAsync(target, trunk, branch), inspect: true)
        : Task.FromResult(CommandOutcome.Refused("Load GitHub CLI stack support first."));

    public Task<CommandOutcome> AddGhLayerAsync(string branch) => CanAddGhLayer
        ? RunGhStackAsync((client, target) => client.AddAsync(target, branch), inspect: true)
        : Task.FromResult(CommandOutcome.Refused("Check out the GitHub CLI stack tip first."));

    public async Task<Result<GhStackRebasePlan>> PreviewGhStackAsync()
    {
        if (!CanRebaseGhStack || _ghStacks is null || Repository is not { } repository)
            return Result<GhStackRebasePlan>.Fail(Problem.Of(ProblemCodes.StateInvalid, "Load the current GitHub CLI stack to preview its upper layers."));
        IsGhStackBusy = true;
        try { return await _ghStacks.PreviewRebaseAsync(StackTarget(repository)); }
        finally { IsGhStackBusy = false; }
    }

    public Task<CommandOutcome> RebaseGhStackAsync(GhStackRebasePlan plan) => CanRebaseGhStack && Repository?.Root == plan.Target.Root
        ? RunGhStackAsync((client, _) => client.RebaseAsync(plan), inspect: true)
        : Task.FromResult(CommandOutcome.Refused("Load the current GitHub CLI stack and open a new preview."));

    public Task<CommandOutcome> RecoverGhStackAsync(bool abort) => GhStackRecovery is { } recovery && (abort ? CanAbortGhStack : CanContinueGhStack)
        ? RunGhStackAsync((client, target) => client.RecoverAsync(target, recovery.OperationId, abort), inspect: false)
        : Task.FromResult(CommandOutcome.Refused("Refresh the stopped GitHub CLI rebase first."));

    public Task<CommandOutcome> AcknowledgeGhStackAsync() => CanAcknowledgeGhStack && GhStackRecovery is { } recovery
        ? RunGhStackAsync((client, target) => client.AcknowledgeAsync(target.Root, recovery.OperationId), inspect: false)
        : Task.FromResult(CommandOutcome.Refused("Finish the current Git operation first."));

    private async Task<CommandOutcome> RunGhStackAsync(Func<IGhStackClient, GhStackTarget, Task<CommandOutcome>> run, bool inspect)
    {
        if (_ghStacks is null || Repository is not { } repository || IsSyncing)
            return CommandOutcome.Refused("Wait for the current operation or open a repository.");
        IsSyncing = true;
        try
        {
            var result = await run(_ghStacks, StackTarget(repository));
            Status = result.Message;
            await ReloadLocalAsync();
            await RefreshGhStackRecoveryAsync();
            if (UsesGhStack && GhStackRecovery is null && result.Ok)
                await ReadGhStackAsync(inspect);
            if (IsWorkingTreeOpen && WorkingTreeEditor is not null)
                await WorkingTreeEditor.RefreshAsync();
            return result;
        }
        catch (Exception ex) { return CommandOutcome.Failed(Boundary.Problem(ex, ProblemCodes.GitFailed)); }
        finally { IsSyncing = false; }
    }

    internal async Task RefreshGhStackRecoveryAsync(CancellationToken ct = default)
    {
        if (_ghStacks is null || Repository is not { IsDemo: false } repository)
            return;
        var result = await _ghStacks.RecoveryAsync(repository.Root, ct);
        if (ct.IsCancellationRequested || Repository?.Root != repository.Root)
            return;
        if (result.IsOk)
            GhStackRecovery = result.Value;
        else
            Error = result.Problem!.Message;
    }

    private GhStackTarget StackTarget(RepositorySnapshot repository)
    {
        var context = _runtime._remoteSnapshot?.Context;
        return new(repository.Root, GitHubPreferences.GitHubOverride ?? (context is null ? null : $"{context.Host}/{context.Owner}/{context.Name}"));
    }

    partial void OnGhStackRecoveryChanged(GhStackRecovery? value)
    {
        NotifyGhStackActions();
        OnPropertyChanged(nameof(HasRecovery));
    }

    partial void OnIsGhStackBusyChanged(bool value) => NotifyGhStackActions();

    internal void NotifyGhStackActions()
    {
        OnPropertyChanged(nameof(HasGhStackBackend));
        OnPropertyChanged(nameof(UsesGhStack));
        OnPropertyChanged(nameof(CanChooseStackBackend));
        OnPropertyChanged(nameof(CanManageStackerStacks));
        OnPropertyChanged(nameof(CanEditStackerSelection));
        OnPropertyChanged(nameof(CanRestackActiveStack));
        OnPropertyChanged(nameof(CanLoadGhStack));
        OnPropertyChanged(nameof(CanCreateGhStack));
        OnPropertyChanged(nameof(CanAddGhLayer));
        OnPropertyChanged(nameof(CanRebaseGhStack));
        OnPropertyChanged(nameof(HasGhStackRecovery));
        OnPropertyChanged(nameof(CanContinueGhStack));
        OnPropertyChanged(nameof(CanAbortGhStack));
        OnPropertyChanged(nameof(CanAcknowledgeGhStack));
    }
}
