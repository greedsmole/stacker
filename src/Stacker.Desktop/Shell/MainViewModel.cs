using Stacker.Core.Framework;
// Purpose: Central screen state and comparison navigation; turns stack/layer selection into explicit diff requests.
using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;


namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable, ISyncActions, IWorkspacePresentation
{
    private readonly WorkspaceRuntime _runtime = new();
    private readonly SyncController _syncController;
    private readonly WorkspaceController _workspaceController;
    private readonly GitHubController _gitHubController;
    private readonly WorkingTreeController _workingTreeController;
    private readonly SearchController _searchController;
    private readonly NavigationController _navigationController;
    private readonly GitLogController _gitLogController;
    public void Notify(string name) => OnPropertyChanged(name);

    public IReadOnlyList<int> SelectedPositions => _runtime._selectedPositions;
    public AppSettings Settings { get; set; } = new();
    public IWorkspaceSession Session => _runtime.Session;
    public RepositorySnapshot? Repository => _runtime._repository;
    public ObservableCollection<StackDefinition> Stacks { get; } = [];
    public ObservableCollection<StackGroupViewModel> LocalGroups { get; } = [];
    public ObservableCollection<LayerViewModel> Layers { get; } = [];
    public ObservableCollection<DiffSectionViewModel> Sections { get; } = [];
    public ObservableCollection<string> RecentRepositories { get; } = [];
    [ObservableProperty] internal string _repositoryPath = "";
    [ObservableProperty] internal string _repositoryTitle = "Open a repository to get started";
    [ObservableProperty] internal string _status = "Local Git · read-only history";
    [ObservableProperty] internal string _error = "";
    [ObservableProperty] internal string _emptyMessage = "Open a repository or explore the demo.";
    [ObservableProperty] internal bool _isBusy;
    [ObservableProperty] internal bool _canEdit;
    [ObservableProperty] internal StackDefinition? _selectedStack;
    [ObservableProperty] internal DiffMode _mode = DiffMode.FullStack;
    [ObservableProperty] internal string _comparisonTitle = "Changes";
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasNoSections => Sections.Count == 0;
    public bool HasSelectedLayer => _runtime._selectedPositions.Length == 1;
    public bool IsLocalSelected => _runtime._activeGroup is { IsRemote: false } && CanEdit;
    /// <summary>Packaged product version, read from the assembly so the status bar cannot drift from the release.</summary>
    public static string ProductVersion => "Stacker " + (typeof(MainViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev");
    public string BaseLabel => _runtime._activeGroup?.BaseLabel ?? "No stack selected";
    public MainViewModel(IGitRepositoryReader git, IStackStore store, ISettingsStore settingsStore, IGitToolSettings executable, StackService stacks, DiffService diff,
        IGitHubReader? github = null, IGitHubWriter? writer = null, IPullRequestObjects? cache = null, IApplicationState? applicationStore = null,
        ISyntaxHighlighter? highlighter = null, IDemoRepository? demo = null, IGhToolSettings? ghExecutable = null, IGitDiagnosticLog? commandLog = null,
        IGitBranchWriter? branchWriter = null, IGitHubPullRequestWriter? pullRequestWriter = null, IEditorLauncher? editorLauncher = null,
        CommandBus? commands = null, IHistoryStore? historyStore = null, IHistoryGit? historyObjects = null,
        ChangeGraphService? historyGraph = null, VersionComparisonService? historyComparisons = null, ReviewProgressService? historyProgress = null, IWorkspaceBackupService? backupService = null, IChangeIdentityService? identities = null, IDiagnosticSink? diagnostics = null)
    {
        _backupService = backupService; _runtime.Identities=identities; _runtime.Diagnostics=diagnostics;
        _runtime.HistoryStore = historyStore; _runtime.HistoryObjects = historyObjects; _runtime.HistoryGraph = historyGraph; _runtime.HistoryComparisons = historyComparisons; _runtime.HistoryProgress = historyProgress;
        _syncController = new(this, _runtime);
        _workspaceController = new(this, _runtime);
        _gitHubController = new(this, _runtime);
        _workingTreeController = new(this, _runtime);
        _searchController = new(this, _runtime);
        _navigationController = new(this, _runtime);
        _gitLogController = new(this, _runtime);

        _runtime._git = git; _runtime._store = store; _runtime._settingsStore = settingsStore; _runtime._executable = executable; _runtime._stacks = stacks; _runtime._diff = diff; _runtime._highlighter = highlighter;
        _runtime._branchWriter = branchWriter; _runtime._pullRequestWriter = pullRequestWriter; _runtime._editorLauncher = editorLauncher;
        _runtime._github = github; _runtime._writer = writer; _runtime._prObjects = cache; _runtime._applicationStore = applicationStore ?? new MemoryApplicationState(); _runtime._workspacePersistence = new(_runtime._applicationStore); _runtime._demo = demo; _runtime._ghExecutable = ghExecutable;
        _runtime._commandLog = commandLog;
        _runtime._commands = commands;
        if (_runtime._commands is not null) { _runtime._commands.Reported += ReportCommand; _runtime._commands.Starting += CommandStarting; }
        if (_runtime._commandLog is not null) { foreach (var entry in _runtime._commandLog.Snapshot()) GitLogEntries.Add(entry); _runtime._commandLog.Changed += LogChanged; }
        Sections.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoSections));
            // Search results point at sections; when the sections go, the results must go with them.
            if (Sections.Count == 0) ResetSearch();
        };
    }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSelectedStackChanged(StackDefinition? value)
    {
        if (!_runtime._suppress && value is not null && LocalGroups.FirstOrDefault(g => g.Definition.Id == value.Id) is { } group) _ = SelectGroupAsync(group);
    }
    partial void OnModeChanged(DiffMode value) { OnPropertyChanged(nameof(IsSingleLayer)); OnPropertyChanged(nameof(IsThroughLayer)); if (!_runtime._suppress) { CaptureWorkspace(); _ = CompareAsync(); } }
    /// <summary>Restores preferences and the last workspace once when the application starts.</summary>
    public Task InitializeAsync()=>_navigationController.InitializeAsync();
    [RelayCommand] public Task OpenAsync() => _navigationController.OpenAsync();
    [RelayCommand] public Task RefreshAsync() => _navigationController.RefreshAsync();
    /// <summary>Starts a new repository workspace; unlike refresh, this intentionally replaces the active repository.</summary>
    public Task OpenRepositoryAsync(string path) { CloseHistory(); return _navigationController.OpenRepositoryAsync(path); }
    // Opening and refreshing share the data-loading path, but only an explicit open clears workspace state.
    internal Task ReadRepositoryAsync(string path, bool opening) => _navigationController.ReadRepositoryAsync(path, opening);
    public void SetActiveGroup(StackGroupViewModel group) => _navigationController.SetActiveGroup(group);
    /// <summary>Selects a stack overview and requests its final layer as the complete stack comparison.</summary>
    public async Task SelectGroupAsync(StackGroupViewModel group, bool saveCurrent = true) { await _navigationController.SelectGroupAsync(group, saveCurrent); await SynchronizeHistoryAsync(); }
    /// <summary>Selects one layer or toggles it into a same-stack multi-layer selection.</summary>
    /// <remarks>Multi-layer comparisons remain independent and keep each selected layer's real parent branch.</remarks>
    public async Task SelectLayerAsync(LayerViewModel layer, bool toggle) { await _navigationController.SelectLayerAsync(layer, toggle); await SynchronizeHistoryAsync(); }
    public void SelectLayers(IEnumerable<LayerViewModel> layers) => _navigationController.SelectLayers(layers);
    /// <summary>Rebuilds visible sections from the selected stack, comparison mode, and captured ref snapshot.</summary>
    /// <remarks>Cancellation and comparison keys prevent a slower previous selection from replacing a newer one.</remarks>
    public Task CompareAsync() => _navigationController.CompareAsync();
    internal static string ComparisonKey(RepositorySnapshot repo, StackDefinition stack, DiffMode mode, int[] positions)
    {
        var relevant = stack.Branches.Prepend(stack.Base).ToHashSet();
        return StableIdentity.For(new[] { repo.Scope, stack.Id, mode.ToString(), string.Join(',', positions), stack.Branches.Count.ToString() }.Concat(stack.Branches.Prepend(stack.Base)).Concat(repo.Refs.Where(r => relevant.Contains(r.Name)).OrderBy(r => r.Name).SelectMany(r => new[] { r.Name, r.Sha })).ToArray());
    }
    /// <summary>Creates or updates one local stack definition without modifying its underlying Git branches.</summary>
    public Task<bool> SaveStackAsync(StackDefinition stack) => _navigationController.SaveStackAsync(stack);
    /// <summary>Removes only the selected description; refs and commits remain untouched.</summary>
    public Task DeleteSelectedStackAsync() => _navigationController.DeleteSelectedStackAsync();
    internal Task<bool> SaveStacksAsync(IReadOnlyList<StackDefinition> stacks) => _navigationController.SaveStacksAsync(stacks);
    public Task SaveSettingsAsync()=>_navigationController.SaveSettingsAsync();
    public Task SetWrapCodeAsync(bool enabled) => _navigationController.SetWrapCodeAsync(enabled);
    public void DisposeViews() => _navigationController.DisposeViews();
    public void Dispose() { ActiveHistory?.Dispose(); ActiveHistory=null; _runtime.Session.Suspend(); _navigationController.Dispose(); }
}
