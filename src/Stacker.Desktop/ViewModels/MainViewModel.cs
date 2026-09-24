using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Infrastructure;

namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGitRepositoryReader _git;
    private readonly IStackStore _store;
    private readonly ISettingsStore _settingsStore;
    private readonly GitExecutable _executable;
    private readonly StackService _stacks;
    private readonly DiffService _diff;
    private readonly ISyntaxHighlighter? _highlighter;
    private StackDocument? _document;
    private RepositorySnapshot? _repository;
    private CancellationTokenSource? _refresh;
    private CancellationTokenSource? _comparison;
    private bool _suppress;
    private bool _disposed;
    private StackGroupViewModel? _activeGroup;
    private readonly Dictionary<string, List<DiffSectionViewModel>> _views = [];
    private readonly Dictionary<string, (DiffMode Mode, string[] Branches)> _savedSelections = [];
    private int[] _selectedPositions = [];
    public bool HasSavedSelection => _activeGroup is not null && _savedSelections.ContainsKey(_activeGroup.Definition.Id) && Mode == DiffMode.FullStack;
    public IReadOnlyList<int> SelectedPositions => _selectedPositions;
    public AppSettings Settings { get; private set; } = new();
    public RepositorySnapshot? Repository => _repository;
    public ObservableCollection<StackDefinition> Stacks { get; } = [];
    public ObservableCollection<StackGroupViewModel> LocalGroups { get; } = [];
    public ObservableCollection<LayerViewModel> Layers { get; } = [];
    public ObservableCollection<DiffSectionViewModel> Sections { get; } = [];
    public ObservableCollection<string> RecentRepositories { get; } = [];
    [ObservableProperty] private string _repositoryPath = "";
    [ObservableProperty] private string _repositoryTitle = "Open a repository to get started";
    [ObservableProperty] private string _status = "Local Git · read-only history";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _emptyMessage = "Open a repository or explore the demo.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private StackDefinition? _selectedStack;
    [ObservableProperty] private DiffMode _mode = DiffMode.FullStack;
    [ObservableProperty] private string _comparisonTitle = "Changes";
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasNoSections => Sections.Count == 0;
    public bool HasSelectedLayer => _selectedPositions.Length == 1;
    public bool IsLocalSelected => _activeGroup is { IsRemote: false } && CanEdit;
    public string BaseLabel => _activeGroup?.BaseLabel ?? "No stack selected";
    public MainViewModel(IGitRepositoryReader git, IStackStore store, ISettingsStore settingsStore, GitExecutable executable, StackService stacks, DiffService diff,
        IGitHubReader? github = null, IGitHubWriter? writer = null, IGitObjectCache? cache = null, ApplicationStore? applicationStore = null,
        ISyntaxHighlighter? highlighter = null, DemoRepositoryGenerator? demo = null, GhExecutable? ghExecutable = null)
    {
        _git = git; _store = store; _settingsStore = settingsStore; _executable = executable; _stacks = stacks; _diff = diff; _highlighter = highlighter;
        _github = github; _writer = writer; _objectCache = cache; _applicationStore = applicationStore ?? new(); _demo = demo; _ghExecutable = ghExecutable;
        Sections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoSections));
    }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSelectedStackChanged(StackDefinition? value)
    {
        if (!_suppress && value is not null && LocalGroups.FirstOrDefault(g => g.Definition.Id == value.Id) is { } group) _ = SelectGroupAsync(group);
    }
    partial void OnModeChanged(DiffMode value) { if (!_suppress) _ = CompareAsync(); }
    public async Task InitializeAsync()
    {
        try
        {
            Settings = await _settingsStore.LoadAsync(); _executable.Override = Settings.GitPath;
            if (_ghExecutable is not null) { _ghExecutable.Override = Settings.GhPath; _ghExecutable.UseSavedCredentials = Settings.UseSavedGhCredentials; }
            foreach (var path in Settings.RecentRepositories.Take(10)) RecentRepositories.Add(path);
        }
        catch (Exception ex) { Error = "Cannot load settings: " + ex.Message; }
    }
    [RelayCommand] public Task OpenAsync() => OpenRepositoryAsync(RepositoryPath);
    [RelayCommand] public Task RefreshAsync() => ReadRepositoryAsync(_repository?.Root ?? RepositoryPath, false);
    public Task OpenRepositoryAsync(string path) => ReadRepositoryAsync(path, true);
    private async Task ReadRepositoryAsync(string path, bool opening)
    {
        if (string.IsNullOrWhiteSpace(path) || _disposed) return;
        _refresh?.Cancel(); var refresh = new CancellationTokenSource(); _refresh = refresh;
        IsBusy = true; Error = "";
        try
        {
            var repo = await _git.OpenAsync(path, refresh.Token);
            var document = await _store.LoadAsync(repo.Root, refresh.Token);
            var sameRepo = _repository?.Root == repo.Root;
            var unchanged = sameRepo && _document?.Revision == document.Revision && _repository!.Refs.SequenceEqual(repo.Refs);
            var groups = new List<StackGroupViewModel>();
            if (!unchanged)
                foreach (var stack in document.Stacks) groups.Add(new(stack, await _stacks.SnapshotAsync(repo, stack, refresh.Token)) { IsExpanded = !sameRepo || LocalGroups.FirstOrDefault(g => g.Definition.Id == stack.Id)?.IsExpanded != false });
            refresh.Token.ThrowIfCancellationRequested();
            var previousGroup = _activeGroup; var selectedBranches = previousGroup?.Layers.Where(l => _selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch).ToHashSet() ?? []; var mode = Mode;
            if (!sameRepo) { _comparison?.Cancel(); _githubLoad?.Cancel(); DisposeViews(); _savedSelections.Clear(); Sections.Clear(); RemoteGroups.Clear(); OtherGroups.Clear(); _remoteSnapshot = null; _activeGroup = null; GitHubStatus = "Not connected"; }
            _repository = repo; _document = document; RepositoryPath = repo.Root;
            IsDemo = File.Exists(Path.Combine(repo.Root, ".stacker-demo.json"));
            RepositoryTitle = Path.GetFileName(repo.Root) + (IsDemo ? " · Demo / offline" : "");
            Status = $"Working tree: {repo.ChangedFiles} changed entries · Updated {DateTime.Now:HH:mm:ss}"; CanEdit = true;
            if (!unchanged)
            {
                Stacks.Clear(); LocalGroups.Clear();
                foreach (var group in groups)
                {
                    for (var i = 0; i < group.Layers.Count; i++)
                        if (FindLocalPr(group.Layers[i].Snapshot.Branch) is { } pr) group.Layers[i] = new(group.Layers[i].Snapshot, pr) { Group = group };
                    Stacks.Add(group.Definition); LocalGroups.Add(group);
                }
                var selected = sameRepo && previousGroup?.IsRemote == true ? previousGroup : groups.FirstOrDefault(g => g.Definition.Id == previousGroup?.Definition.Id) ?? groups.FirstOrDefault();
                if (selected is not null)
                {
                    SetActiveGroup(selected);
                    _suppress = true; Mode = sameRepo && selected.Definition.Id == previousGroup?.Definition.Id ? mode : DiffMode.FullStack;
                    _selectedPositions = sameRepo ? selected.Layers.Where(l => selectedBranches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray() : [];
                    if (Mode != DiffMode.FullStack && _selectedPositions.Length == 0) { Mode = DiffMode.FullStack; Error = "The selected layer disappeared. Showing the entire stack."; }
                    _suppress = false; await CompareAsync();
                }
                else { _activeGroup = null; _suppress = true; SelectedStack = null; _suppress = false; Layers.Clear(); Sections.Clear(); EmptyMessage = "Create a stack, or connect GitHub to discover PRs."; }
            }
            if (opening)
            {
                Settings.RecentRepositories.Remove(repo.Root); Settings.RecentRepositories.Insert(0, repo.Root); Settings.RecentRepositories = Settings.RecentRepositories.Take(10).ToList();
                RecentRepositories.Clear(); foreach (var recent in Settings.RecentRepositories) RecentRepositories.Add(recent);
                await SaveSettingsAsync();
                if (!sameRepo) await RefreshGitHubAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!refresh.IsCancellationRequested) Error = ex.Message + (_repository is null ? "" : " Previous data retained."); }
        finally { if (ReferenceEquals(_refresh, refresh)) { _refresh = null; IsBusy = false; } refresh.Dispose(); }
    }
    private void SetActiveGroup(StackGroupViewModel group)
    {
        _comparison?.Cancel();
        foreach (var g in LocalGroups.Concat(RemoteGroups).Concat(OtherGroups)) { g.IsSelected = ReferenceEquals(g, group); foreach (var l in g.Layers) { l.IsSelected = false; l.IsChecked = false; } }
        _activeGroup = group; _suppress = true; SelectedStack = group.Definition; _suppress = false;
        Layers.Clear(); foreach (var layer in group.Layers) Layers.Add(layer);
        OnPropertyChanged(nameof(BaseLabel)); OnPropertyChanged(nameof(IsLocalSelected));
    }
    public async Task SelectGroupAsync(StackGroupViewModel group)
    {
        SetActiveGroup(group); _selectedPositions = []; _suppress = true; Mode = DiffMode.FullStack; _suppress = false; await CompareAsync();
    }
    public async Task SelectLayerAsync(LayerViewModel layer, bool toggle)
    {
        var group = layer.Group!;
        if (!ReferenceEquals(_activeGroup, group)) { SetActiveGroup(group); _selectedPositions = []; }
        _selectedPositions = toggle ? (_selectedPositions.Contains(layer.Snapshot.Position) ? _selectedPositions.Where(p => p != layer.Snapshot.Position) : _selectedPositions.Append(layer.Snapshot.Position)).Order().ToArray() : [layer.Snapshot.Position];
        _suppress = true; Mode = _selectedPositions.Length == 0 ? DiffMode.FullStack : _selectedPositions.Length == 1 ? DiffMode.Layer : DiffMode.MultiLayer; _suppress = false;
        await CompareAsync();
    }
    public void SelectLayers(IEnumerable<LayerViewModel> layers)
    {
        _selectedPositions = layers.Select(l => l.Snapshot.Position).Order().ToArray();
        _suppress = true; Mode = _selectedPositions.Length > 1 ? DiffMode.MultiLayer : DiffMode.Layer; _suppress = false; _ = CompareAsync();
    }
    [RelayCommand] public async Task RestoreSelectionAsync()
    {
        if (_activeGroup is null || !_savedSelections.TryGetValue(_activeGroup.Definition.Id, out var saved)) return;
        _selectedPositions = _activeGroup.Layers.Where(l => saved.Branches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray();
        _suppress = true; Mode = _selectedPositions.Length == 0 ? DiffMode.FullStack : _selectedPositions.Length > 1 ? DiffMode.MultiLayer : saved.Mode == DiffMode.Cumulative ? DiffMode.Cumulative : DiffMode.Layer; _suppress = false;
        await CompareAsync();
    }
    public async Task CompareAsync()
    {
        _comparison?.Cancel(); var compare = new CancellationTokenSource(); _comparison = compare;
        var group = _activeGroup; var repo = _repository; var mode = Mode; var positions = _selectedPositions.ToArray();
        if (repo is null || group is null || _disposed) { compare.Dispose(); if (ReferenceEquals(_comparison, compare)) _comparison = null; return; }
        OnPropertyChanged(nameof(HasPrDiscussion));
        IsBusy = true; Error = "";
        try
        {
            var comparisonRepository = repo; var stack = group.Definition;
            if (group.IsRemote)
            {
                if (_remoteSnapshot is null) throw new StackerException("Refresh GitHub to load this stack.");
                comparisonRepository = await PrepareRemoteAsync(group, compare.Token);
                stack = new(group.Definition.Id, group.Name, GitObjectCache.BaseRef(group.PullRequests[0]), group.PullRequests.Select(GitObjectCache.HeadRef).ToArray());
                // For a single PR's review use its declared server base, even if the discovered parent has moved.
                if (mode == DiffMode.Layer && positions.Length == 1)
                {
                    var pr = group.PullRequests[positions[0]];
                    stack = new(stack.Id, stack.Name, GitObjectCache.BaseRef(pr), [GitObjectCache.HeadRef(pr)]); positions = [0];
                }
            }
            var key = ComparisonKey(comparisonRepository, stack, mode, positions) + "|" + Settings.Theme;
            if (!_views.TryGetValue(key, out var sections))
            {
                var results = await _diff.CompareAsync(new(comparisonRepository, stack, mode, positions), compare.Token);
                compare.Token.ThrowIfCancellationRequested();
                sections = results.Select(result =>
                {
                    var pr = group.IsRemote ? group.PullRequests.FirstOrDefault(p => GitObjectCache.HeadRef(p) == result.Layer) : FindLocalPr(result.Layer);
                    return new DiffSectionViewModel(_git, comparisonRepository.Root, result, _highlighter, Settings.Theme)
                    { AssociatedPr = pr, GitHubContext = _remoteSnapshot?.Context, IsPrSnapshot = group.IsRemote && mode == DiffMode.Layer, IsDemo = IsDemo };
                }).ToList();
                // Carry file/filter/scroll state across a changed snapshot of the same comparison.
                foreach (var section in sections)
                {
                    var old = Sections.FirstOrDefault(s => s.Result.Layer == section.Result.Layer);
                    if (old is not null) section.RestoreView(old);
                }
                _views[key] = sections;
                while (_views.Count > 24) { var victim = _views.First(kv => kv.Key != key && !kv.Value.Any(Sections.Contains)); foreach (var s in victim.Value) s.Dispose(); _views.Remove(victim.Key); }
            }
            compare.Token.ThrowIfCancellationRequested();
            if (!Sections.SequenceEqual(sections)) { Sections.Clear(); foreach (var section in sections) Sections.Add(section); }
            foreach (var layer in group.Layers) { layer.IsChecked = _selectedPositions.Contains(layer.Snapshot.Position); layer.IsSelected = layer.IsChecked; }
            ComparisonTitle = mode switch
            {
                DiffMode.FullStack => $"Entire stack · {group.Name}",
                DiffMode.Cumulative => $"Through layer {(_selectedPositions.FirstOrDefault() + 1)} · {group.Name}",
                DiffMode.MultiLayer => $"Selected layers · {string.Join(", ", _selectedPositions.Select(p => p + 1))}",
                _ => $"Layer {(_selectedPositions.FirstOrDefault() + 1)} · {group.Layers.ElementAtOrDefault(_selectedPositions.FirstOrDefault())?.Title}"
            };
            if (_github is not null) foreach (var section in sections) _ = section.LoadThreadsAsync(_github);
            if (mode != DiffMode.FullStack) _savedSelections[group.Definition.Id] = (mode, group.Layers.Where(l => _selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch).ToArray());
            OnPropertyChanged(nameof(HasSavedSelection));
            OnPropertyChanged(nameof(HasSelectedLayer));
            EmptyMessage = sections.Count == 0 ? "This stack has no layers. Edit it to add branches." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!compare.IsCancellationRequested) Error = ex.Message; }
        finally { if (ReferenceEquals(_comparison, compare)) { _comparison = null; IsBusy = false; } compare.Dispose(); }
    }
    private static string ComparisonKey(RepositorySnapshot repo, StackDefinition stack, DiffMode mode, int[] positions)
    {
        var relevant = stack.Branches.Prepend(stack.Base).ToHashSet();
        return repo.Root + "|" + stack.Id + "|" + mode + "|" + string.Join(',', positions) + "|" + string.Join(';', stack.Branches.Prepend(stack.Base)) + "|" + string.Join(';', repo.Refs.Where(r => relevant.Contains(r.Name)).OrderBy(r => r.Name).Select(r => r.Name + "=" + r.Sha));
    }
    public async Task<bool> SaveStackAsync(StackDefinition stack)
    {
        if (_repository is null || _document is null) return false;
        var updated = _document.Stacks.Any(s => s.Id == stack.Id) ? _document.Stacks.Select(s => s.Id == stack.Id ? stack : s).ToArray() : _document.Stacks.Append(stack).ToArray();
        return await SaveStacksAsync(updated);
    }
    public async Task DeleteSelectedStackAsync()
    {
        if (_document is not null && SelectedStack is not null && _activeGroup?.IsRemote != true) await SaveStacksAsync(_document.Stacks.Where(s => s.Id != SelectedStack.Id).ToArray());
    }
    private async Task<bool> SaveStacksAsync(IReadOnlyList<StackDefinition> stacks)
    {
        if (_repository is null || _document is null) return false;
        try { await _store.SaveAsync(_repository.Root, stacks, _document.Revision); await RefreshAsync(); return !HasError; }
        catch (Exception ex) { Error = ex.Message; return false; }
    }
    public async Task SaveSettingsAsync()
    {
        try { _executable.Override = Settings.GitPath; if (_ghExecutable is not null) { _ghExecutable.Override = Settings.GhPath; _ghExecutable.UseSavedCredentials = Settings.UseSavedGhCredentials; } await _settingsStore.SaveAsync(Settings); }
        catch (Exception ex) { Error = "Cannot save settings: " + ex.Message; }
    }
    private void DisposeViews() { foreach (var section in _views.Values.SelectMany(v => v).Distinct()) section.Dispose(); _views.Clear(); }
    public void Dispose() { _disposed = true; _refresh?.Cancel(); _comparison?.Cancel(); _githubLoad?.Cancel(); DisposeViews(); }
}
