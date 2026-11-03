using Stacker.Core.Framework;
// Purpose: Central screen state and comparison navigation; turns stack/layer selection into explicit diff requests.
using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;


namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable, ISyncActions
{
    private readonly SyncController _syncController;
    private readonly WorkspaceController _workspaceController;
    private readonly GitHubController _gitHubController;
    private readonly WorkingTreeController _workingTreeController;
    private readonly SearchController _searchController;
    private readonly NavigationController _navigationController;
    private readonly GitLogController _gitLogController;
    internal void Notify(string name) => OnPropertyChanged(name);

    internal readonly IGitRepositoryReader _git;
    internal readonly IStackStore _store;
    internal readonly ISettingsStore _settingsStore;
    internal readonly IGitToolSettings _executable;
    internal readonly StackService _stacks;
    internal readonly DiffService _diff;
    internal readonly ISyntaxHighlighter? _highlighter;
    internal StackDocument? _document;
    internal RepositorySnapshot? _repository;
    internal CancellationTokenSource? _refresh;
    internal CancellationTokenSource? _comparison;
    internal bool _suppress;
    internal bool _disposed;
    internal StackGroupViewModel? _activeGroup;
    internal readonly Dictionary<string, List<DiffSectionViewModel>> _views = [];
    internal int[] _selectedPositions = [];
    public IReadOnlyList<int> SelectedPositions => _selectedPositions;
    public AppSettings Settings { get; internal set; } = new();
    public RepositorySnapshot? Repository => _repository;
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
    public bool HasSelectedLayer => _selectedPositions.Length == 1;
    public bool IsLocalSelected => _activeGroup is { IsRemote: false } && CanEdit;
    /// <summary>Packaged product version, read from the assembly so the status bar cannot drift from the release.</summary>
    public static string ProductVersion => "Stacker " + (typeof(MainViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev");
    public string BaseLabel => _activeGroup?.BaseLabel ?? "No stack selected";
    public MainViewModel(IGitRepositoryReader git, IStackStore store, ISettingsStore settingsStore, IGitToolSettings executable, StackService stacks, DiffService diff,
        IGitHubReader? github = null, IGitHubWriter? writer = null, IGitObjectCache? cache = null, IApplicationState? applicationStore = null,
        ISyntaxHighlighter? highlighter = null, IDemoRepository? demo = null, IGhToolSettings? ghExecutable = null, IGitDiagnosticLog? commandLog = null,
        IGitBranchWriter? branchWriter = null, IGitHubPullRequestWriter? pullRequestWriter = null, IEditorLauncher? editorLauncher = null,
        CommandBus? commands = null, IHistoryStore? historyStore = null, IHistoryGit? historyObjects = null,
        ChangeGraphService? historyGraph = null, VersionComparisonService? historyComparisons = null, ReviewProgressService? historyProgress = null)
    {
        HistoryStore = historyStore; HistoryObjects = historyObjects; HistoryGraph = historyGraph; HistoryComparisons = historyComparisons; HistoryProgress = historyProgress;
        _syncController = new(this);
        _workspaceController = new(this);
        _gitHubController = new(this);
        _workingTreeController = new(this);
        _searchController = new(this);
        _navigationController = new(this);
        _gitLogController = new(this);

        _git = git; _store = store; _settingsStore = settingsStore; _executable = executable; _stacks = stacks; _diff = diff; _highlighter = highlighter;
        _branchWriter = branchWriter; _pullRequestWriter = pullRequestWriter; _editorLauncher = editorLauncher;
        _github = github; _writer = writer; _objectCache = cache; _applicationStore = applicationStore ?? new MemoryApplicationState(); _workspacePersistence = new(_applicationStore); _demo = demo; _ghExecutable = ghExecutable;
        _commandLog = commandLog;
        _commands = commands;
        if (_commands is not null) { _commands.Reported += ReportCommand; _commands.Starting += CommandStarting; }
        if (_commandLog is not null) { foreach (var entry in _commandLog.Snapshot()) GitLogEntries.Add(entry); _commandLog.Changed += LogChanged; }
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
        if (!_suppress && value is not null && LocalGroups.FirstOrDefault(g => g.Definition.Id == value.Id) is { } group) _ = SelectGroupAsync(group);
    }
    partial void OnModeChanged(DiffMode value) { OnPropertyChanged(nameof(IsSingleLayer)); OnPropertyChanged(nameof(IsThroughLayer)); if (!_suppress) { CaptureWorkspace(); _ = CompareAsync(); } }
    /// <summary>Restores preferences and the last workspace once when the application starts.</summary>
    public Task InitializeAsync() => _navigationController.InitializeAsync();
    [RelayCommand] public Task OpenAsync() => _navigationController.OpenAsync();
    [RelayCommand] public Task RefreshAsync() => _navigationController.RefreshAsync();
    /// <summary>Starts a new repository workspace; unlike refresh, this intentionally replaces the active repository.</summary>
    public Task OpenRepositoryAsync(string path) => _navigationController.OpenRepositoryAsync(path);
    // Opening and refreshing share the data-loading path, but only an explicit open clears workspace state.
    internal Task ReadRepositoryAsync(string path, bool opening) => _navigationController.ReadRepositoryAsync(path, opening);
    internal void SetActiveGroup(StackGroupViewModel group) => _navigationController.SetActiveGroup(group);
    /// <summary>Selects a stack overview and requests its final layer as the complete stack comparison.</summary>
    public Task SelectGroupAsync(StackGroupViewModel group, bool saveCurrent = true) => _navigationController.SelectGroupAsync(group, saveCurrent);
    /// <summary>Selects one layer or toggles it into a same-stack multi-layer selection.</summary>
    /// <remarks>Multi-layer comparisons remain independent and keep each selected layer's real parent branch.</remarks>
    public Task SelectLayerAsync(LayerViewModel layer, bool toggle) => _navigationController.SelectLayerAsync(layer, toggle);
    public void SelectLayers(IEnumerable<LayerViewModel> layers) => _navigationController.SelectLayers(layers);
    /// <summary>Rebuilds visible sections from the selected stack, comparison mode, and captured ref snapshot.</summary>
    /// <remarks>Cancellation and comparison keys prevent a slower previous selection from replacing a newer one.</remarks>
    public Task CompareAsync() => _navigationController.CompareAsync();
    internal static string ComparisonKey(RepositorySnapshot repo, StackDefinition stack, DiffMode mode, int[] positions)
    {
        var relevant = stack.Branches.Prepend(stack.Base).ToHashSet();
        return repo.Root + "|" + stack.Id + "|" + mode + "|" + string.Join(',', positions) + "|" + string.Join(';', stack.Branches.Prepend(stack.Base)) + "|" + string.Join(';', repo.Refs.Where(r => relevant.Contains(r.Name)).OrderBy(r => r.Name).Select(r => r.Name + "=" + r.Sha));
    }
    /// <summary>Creates or updates one local stack definition without modifying its underlying Git branches.</summary>
    public Task<bool> SaveStackAsync(StackDefinition stack) => _navigationController.SaveStackAsync(stack);
    /// <summary>Removes only the selected description; refs and commits remain untouched.</summary>
    public Task DeleteSelectedStackAsync() => _navigationController.DeleteSelectedStackAsync();
    internal Task<bool> SaveStacksAsync(IReadOnlyList<StackDefinition> stacks) => _navigationController.SaveStacksAsync(stacks);
    public Task SaveSettingsAsync() => _navigationController.SaveSettingsAsync();
    public Task SetWrapCodeAsync(bool enabled) => _navigationController.SetWrapCodeAsync(enabled);
    internal void DisposeViews() => _navigationController.DisposeViews();
    public void Dispose() => _navigationController.Dispose();
}
