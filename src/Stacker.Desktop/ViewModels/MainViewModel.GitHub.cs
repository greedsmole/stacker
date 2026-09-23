using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IGitHubReader? _github;
    private readonly IGitHubWriter? _writer;
    private readonly IGitObjectCache? _objectCache;
    private readonly ApplicationStore _applicationStore;
    private readonly DemoRepositoryGenerator? _demo;
    private readonly GhExecutable? _ghExecutable;
    private GitHubSnapshot? _remoteSnapshot;
    private CancellationTokenSource? _githubLoad;
    public RepositoryPreferences GitHubPreferences { get; private set; } = new();
    public ObservableCollection<StackGroupViewModel> RemoteGroups { get; } = [];
    public ObservableCollection<StackGroupViewModel> OtherGroups { get; } = [];
    [ObservableProperty] private string _gitHubStatus = "Not connected";
    [ObservableProperty] private bool _gitHubBusy;
    [ObservableProperty] private bool _isDemo;
    public bool HasRemoteGroups => RemoteGroups.Count > 0;
    public bool HasOtherGroups => OtherGroups.Count > 0;
    [RelayCommand] public async Task OpenDemoAsync()
    {
        if (_demo is null) return;
        IsBusy = true;
        try { await OpenRepositoryAsync(await _demo.CreateAsync()); }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand] public async Task RefreshGitHubAsync()
    {
        if (_repository is null) return;
        _githubLoad?.Cancel(); var load = new CancellationTokenSource(); _githubLoad = load;
        var root = _repository.Root; GitHubBusy = true;
        try
        {
            GitHubPreferences = await _applicationStore.ReadAsync<RepositoryPreferences>("repositories", root, load.Token) ?? new();
            GitHubSnapshot snapshot;
            if (IsDemo) snapshot = JsonSerializer.Deserialize<GitHubSnapshot>(await File.ReadAllTextAsync(Path.Combine(root, ".stacker-demo.json"), load.Token))!;
            else
            {
                if (_github is null) return;
                var context = await _github.ConnectAsync(root, GitHubPreferences.GitHubOverride, load.Token);
                snapshot = await _github.SnapshotAsync(context, load.Token);
            }
            load.Token.ThrowIfCancellationRequested();
            _remoteSnapshot = snapshot;
            await _applicationStore.WriteAsync("snapshots", root, snapshot, load.Token);
            GitHubPreferences.BoundaryBranches ??= new[] { snapshot.Context.DefaultBranch }.Concat(new[] { "main", "master", "develop", "test" }.Where(b => _repository!.Refs.Any(r => r.Name.EndsWith('/' + b, StringComparison.Ordinal)) || snapshot.PullRequests.Any(p => p.BaseRef == b || p.HeadRef == b))).Distinct().ToList();
            await _applicationStore.WriteAsync("repositories", root, GitHubPreferences, load.Token);
            BuildDiscoveredGroups();
            GitHubStatus = IsDemo ? "Demo / offline · publishing disabled" : snapshot.Context.Display + $" · Updated {snapshot.UpdatedAt.LocalDateTime:HH:mm}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (load.IsCancellationRequested) return;
            GitHubStatus = ex.Message;
            try
            {
                var saved = await _applicationStore.ReadAsync<GitHubSnapshot>("snapshots", root, load.Token);
                if (saved is not null) { _remoteSnapshot = saved; BuildDiscoveredGroups(); GitHubStatus += $" · Cached {saved.Context.Display} · {saved.UpdatedAt.LocalDateTime:g}"; }
            }
            catch (Exception cacheError) when (cacheError is not OperationCanceledException) { GitHubStatus += " · Cache unavailable"; }
        }
        finally { if (ReferenceEquals(_githubLoad, load)) { _githubLoad = null; GitHubBusy = false; } load.Dispose(); }
    }
    private void BuildDiscoveredGroups()
    {
        if (_remoteSnapshot is null) return;
        var result = StackDiscovery.Discover(_remoteSnapshot.PullRequests, GitHubPreferences.BoundaryBranches ?? [_remoteSnapshot.Context.DefaultBranch]);
        var previous = RemoteGroups.Concat(OtherGroups).ToDictionary(g => g.Definition.Id);
        StackGroupViewModel Retain(StackGroupViewModel next)
        {
            if (!previous.TryGetValue(next.Definition.Id, out var old)) return next;
            if (old.PullRequests.SequenceEqual(next.PullRequests) && old.SharedPrefix == next.SharedPrefix) return old;
            next.IsExpanded = old.IsExpanded; return next;
        }
        RemoteGroups.Clear(); OtherGroups.Clear();
        foreach (var stack in result.Stacks) RemoteGroups.Add(Retain(RemoteGroup(stack.Id, stack.Name, stack.Layers, stack.SharedPrefix)));
        foreach (var pr in result.Other) OtherGroups.Add(Retain(RemoteGroup($"github-{pr.Number}", pr.Title, [pr], false)));
        if (result.Warnings.Count > 0) Error = string.Join("\n", result.Warnings);
        // Keep local stack bindings informative without changing their authored definition.
        foreach (var group in LocalGroups)
            for (var i = 0; i < group.Layers.Count; i++)
            {
                var old = group.Layers[i]; var pr = FindLocalPr(old.Snapshot.Branch);
                if (pr is not null) group.Layers[i] = new(old.Snapshot, pr) { Group = group, IsSelected = old.IsSelected, IsChecked = old.IsChecked };
            }
        foreach (var section in _views.Values.SelectMany(s => s).Where(s => !s.IsPrSnapshot))
            if (FindLocalPr(section.Result.Layer) is { } pr) section.BindPr(pr, _remoteSnapshot.Context);
        if (_activeGroup?.IsRemote == true)
        {
            var replacement = RemoteGroups.Concat(OtherGroups).FirstOrDefault(g => g.Definition.Id == _activeGroup.Definition.Id);
            if (replacement is not null) { SetActiveGroup(replacement); _ = CompareAsync(); }
            else
            {
                _comparison?.Cancel(); _activeGroup = null; _selectedPositions = []; Layers.Clear(); Sections.Clear();
                _suppress = true; SelectedStack = null; _suppress = false;
                EmptyMessage = "The selected PR stack changed or closed. Select an available stack.";
                OnPropertyChanged(nameof(BaseLabel)); OnPropertyChanged(nameof(HasSelectedLayer)); OnPropertyChanged(nameof(IsLocalSelected));
            }
        }
        OnPropertyChanged(nameof(HasRemoteGroups)); OnPropertyChanged(nameof(HasOtherGroups));
    }
    private static StackGroupViewModel RemoteGroup(string id, string name, IReadOnlyList<PullRequest> prs, bool shared)
    {
        var definition = new StackDefinition(id, name, GitObjectCache.BaseRef(prs[0]), prs.Select(GitObjectCache.HeadRef).ToArray());
        var layers = prs.Select((p, i) => new StackLayerSnapshot(i, "refs/heads/" + p.HeadRef, "refs/heads/" + p.BaseRef, p.HeadSha, p.BaseSha, 0, 0, null));
        return new(definition, layers, prs, shared);
    }
    private PullRequest? FindLocalPr(string branch)
    {
        var name = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch.StartsWith("refs/remotes/origin/", StringComparison.Ordinal) ? branch[20..] : "";
        var matches = _remoteSnapshot?.PullRequests.Where(p => p.HeadRepositoryId == _remoteSnapshot.Context.RepositoryId && p.HeadRef == name).ToArray();
        return matches?.Length == 1 ? matches[0] : null;
    }
    private Task<RepositorySnapshot> PrepareRemoteAsync(StackGroupViewModel group, CancellationToken ct)
    {
        if (IsDemo)
        {
            IReadOnlyList<BranchRef> refs = group.PullRequests.SelectMany(p => new[] { new BranchRef(GitObjectCache.HeadRef(p), p.HeadSha), new BranchRef(GitObjectCache.BaseRef(p), p.BaseSha) }).ToArray();
            return Task.FromResult(new RepositorySnapshot(_repository!.Root, refs, 0));
        }
        return _objectCache?.PrepareAsync(_remoteSnapshot!.Context, group.PullRequests, ct) ?? throw new StackerException("GitHub cache is unavailable.");
    }
    public async Task SaveGitHubPreferencesAsync(RepositoryPreferences preferences)
    {
        if (_repository is null) return;
        try { await _applicationStore.WriteAsync("repositories", _repository.Root, preferences); GitHubPreferences = preferences; await RefreshGitHubAsync(); }
        catch (Exception ex) { Error = ex.Message; }
    }
    public async Task ClearGitHubCacheAsync()
    {
        if (_remoteSnapshot is null || _objectCache is null || IsDemo) return;
        try { _comparison?.Cancel(); await _objectCache.ClearAsync(_remoteSnapshot.Context); DisposeViews(); Sections.Clear(); GitHubStatus = "Git object cache cleared. Select a PR to download it again."; }
        catch (Exception ex) { Error = ex.Message; }
    }
    public async Task SaveRemoteAsLocalAsync()
    {
        if (_activeGroup?.IsRemote != true || _repository is null) return;
        try
        {
            var prs = _activeGroup.PullRequests;
            string Resolve(string branch, long repositoryId)
            {
                if (repositoryId != _remoteSnapshot!.Context.RepositoryId) throw new StackerException("Fork refs cannot be mapped to local branches automatically.");
                return new[] { "refs/heads/" + branch, "refs/remotes/origin/" + branch }.FirstOrDefault(r => _repository.Refs.Any(p => p.Name == r)) ?? throw new StackerException($"No local ref for {branch}. Fetch it outside Stacker first.");
            }
            await SaveStackAsync(new(Guid.NewGuid().ToString("N"), _activeGroup.Name, Resolve(prs[0].BaseRef, prs[0].BaseRepositoryId), prs.Select(p => Resolve(p.HeadRef, p.HeadRepositoryId)).ToArray()));
        }
        catch (Exception ex) { Error = ex.Message; }
    }
    public ReviewViewModel? CreateReview(DiffSectionViewModel section) => section.AssociatedPr is { } pr && section.GitHubContext is { } context && _github is not null && _writer is not null
        ? new(_github, _writer, _applicationStore, _git, context, pr, section, IsDemo) : null;
    public async Task OpenPrChangesAsync(DiffSectionViewModel section)
    {
        var pr = section.AssociatedPr; if (pr is null || _remoteSnapshot is null) return;
        var group = RemoteGroups.Concat(OtherGroups).FirstOrDefault(g => g.PullRequests.Any(p => p.Number == pr.Number));
        if (group is null) return;
        var index = group.PullRequests.ToList().FindIndex(p => p.Number == pr.Number);
        await SelectLayerAsync(group.Layers[index], false);
    }
}
