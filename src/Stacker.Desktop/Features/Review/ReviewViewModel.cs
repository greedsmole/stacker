// Purpose: Review workflow state: discussions, file/line comment composers, multi-line anchors, persisted drafts, and explicit publication.
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Desktop.ViewModels;

/// <summary>Owns draft editing and publication for one PR while sharing discussion data with its attached diff views.</summary>
/// <remarks>Drafts are saved locally before network writes so timeout or permission failures do not discard user text.</remarks>
public partial class ReviewViewModel : ObservableObject, IDisposable
{
    private readonly ReviewRecoveryController _reviewRecoveryController;

    internal readonly ReviewDraftController _reviewDraftController;

    internal readonly ReviewPublicationController _reviewPublicationController;
    internal void Notify(string name) => OnPropertyChanged(name);

    internal readonly ReviewSessionService Session;
    internal readonly IGitHubReader _reader;
    internal readonly IGitHubWriter _writer;
    internal readonly IApplicationState _store;
    internal DiffSectionViewModel _section;
    internal readonly HashSet<DiffSectionViewModel> _sections = [];
    internal ComposerKey _composerKey = ComposerKey.Pr;
    internal bool _switchingComposer;
    public string ReviewLabel => $"Review ({DraftComments.Count})";
    public bool HasPendingPublication => _draft.PendingOperation is not null || _draft.Operation is not null;
    public bool HasStaleDraft => _draft.HeadSha != PullRequest.HeadSha || _draft.BaseSha != PullRequest.BaseSha;
    public bool HasRecoveredText => !string.IsNullOrEmpty(RecoveredText);
    public string RecoveredText => _draft.RecoveredText;
    public bool HasSelection => _selection is not null;
    public bool HasSuggestionPreview => ReviewContent.Parse(Composer).Any(b => b.IsSuggestion);
    public string? SelectedCode => GetSelectedCode();
    public bool CanSuggestCodeChange => CanEditDraft && _selection?.Side == "RIGHT" && SelectedCode is not null;
    public string DecisionLabel
    {
        get => Decision switch { "APPROVE" => "Approve", "REQUEST_CHANGES" => "Request changes", _ => "Comment" }; set => Decision = value switch { "Approve" => "APPROVE", "Request changes" => "REQUEST_CHANGES", _ => "COMMENT" };
    }
    public IReadOnlyList<string> DecisionLabels { get; } = ["Comment", "Approve", "Request changes"];
    [ObservableProperty] internal int _panelTab;
    partial void OnPanelTabChanged(int value)
    {
        if (value == 1 || value == 2)
            CloseInlineEditors();
        // The discussion tab always edits the pull-request buffer, never whatever reply was open last.
        if (value == 0)
            SelectComposer("pr");
    }
    public event Action? DiscussionChanged;
    public void Attach(DiffSectionViewModel section)
    {
        if (section.AssociatedPr?.Number != PullRequest.Number || section.GitHubContext?.Identity != Context.Identity)
            return;
        _sections.Add(section);
        section.Review = this;
        section.SetThreads(Threads);
        if (IsBusy)
            return;
        if (!ReferenceEquals(_section, section))
            CloseInlineEditors();
        _section = section;
        if (section.IsPrSnapshot)
        {
            PullRequest = section.AssociatedPr;
            RecoverDetachedEditors();
            _reviewableFiles.Clear();
            foreach (var file in section.Result.Files)
            {
                _reviewableFiles.Add(file.NewPath);
                if (!ReviewFiles.Contains(file.NewPath))
                    ReviewFiles.Add(file.NewPath);
            }
        }
        SelectedReviewFile = section.SelectedFile?.NewPath ?? SelectedReviewFile;
        NotifyPublicationState();
    }
    internal void RecoverDetachedEditors()
=> _reviewRecoveryController.RecoverDetachedEditors();
    public void SelectComposer(string key)
    {
        if (IsBusy)
            return;
        _switchingComposer = true;
        _composerKey = ComposerKey.Parse(key).OrThrow();
        Composer = _draft.ContextComposers.GetValueOrDefault(key, "");
        _switchingComposer = false;
    }
    public void CloseInlineEditors()
    {
        foreach (var view in _sections)
            foreach (var line in view.Lines)
                line.Editor = null;
    }
    public void BeginPrComment()
    {
        if (IsBusy)
            return;
        CloseInlineEditors();
        _selection = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanSuggestCodeChange));
        OnPropertyChanged(nameof(SelectedCode));
        SelectComposer("pr");
        PanelTab = 0;
    }
    public void BeginReply(ReviewThread thread)
    {
        if (IsBusy)
            return;
        CloseInlineEditors();
        _selection = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanSuggestCodeChange));
        OnPropertyChanged(nameof(SelectedCode));
        SelectedThread = thread;
        SelectComposer("reply:" + thread.Id);
        PanelTab = 2;
    }
    [RelayCommand]
    internal void RestoreRecoveredText()
    {
        Composer = string.IsNullOrEmpty(Composer) ? RecoveredText : Composer + "\n\n" + RecoveredText;
        _draft.RecoveredText = "";
        OnPropertyChanged(nameof(RecoveredText));
        OnPropertyChanged(nameof(HasRecoveredText));
        QueueSave();
    }
    [RelayCommand]
    internal void EditDraftComment(DraftComment comment)
    {
        if (IsBusy)
            return;
        CloseInlineEditors();
        _selection = comment.Anchor;
        SelectComposer("line:" + JsonSerializer.Serialize(comment.Anchor));
        Composer = comment.Body;
        RemoveDraftComment(comment);
        PanelTab = 3;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanSuggestCodeChange));
        OnPropertyChanged(nameof(SelectedCode));
        OnPropertyChanged(nameof(SelectionLabel));
    }
    internal bool _loading;
    internal bool _draftLoaded;
    public bool IsInitialized => _draftLoaded;
    public bool CanEditDraft => _draftLoaded && !IsBusy;
    [RelayCommand] internal Task RetryInitializeAsync() => InitializeAsync();
    public void Detach(DiffSectionViewModel section) => _sections.Remove(section);
    internal bool _switchingFile;
    internal readonly HashSet<string> _reviewableFiles = new(StringComparer.Ordinal);
    internal Task _saveQueue = Task.CompletedTask;
    internal string? _saveError;
    internal ReviewDraft _draft = new();
    internal ReviewAnchor? _selection;
    public GitHubRepositoryContext Context
    {
        get;
    }
    public PullRequest PullRequest
    {
        get; internal set;
    }
    public bool IsDemo
    {
        get;
    }
    public bool CanPublish => PublicationBlockReason is null;
    internal string? PublicationBlockReason => IsDemo ? "Demo is offline. Publishing is disabled."
        : !_draftLoaded ? "Load the local review draft before publishing."
        : IsBusy ? "A review action is in progress."
        : HasPendingPublication ? "Check the previous publication on GitHub before sending again."
        : null;
    public string ReviewStatus => IsBusy ? Message
        : PublicationProblem ?? (string.IsNullOrWhiteSpace(Message) ? PublicationBlockReason ?? "" : Message);
    public bool HasReviewStatus => ReviewStatus.Length > 0;
    public bool HasPublicationProblem => PublicationProblem is not null;
    public bool CanPostFile => CanPublish && SelectedReviewFile is not null && _reviewableFiles.Contains(SelectedReviewFile);
    public string FileThreadsLabel => SelectedReviewFile is null ? "Select a file to read its comments." : $"{SelectedReviewFile} · {FileThreads.Count} file threads";
    public string Title => PullRequest.Label + (IsDemo ? " · Demo / offline" : "");
    public string SelectionLabel => _selection is null ? "Select code lines in the diff to attach a comment." : $"{_selection.Path} · {_selection.Side} · lines {_selection.StartLine ?? _selection.Line}–{_selection.Line}";
    public ObservableCollection<DiscussionComment> Comments { get; } = [];
    public ObservableCollection<ReviewSummary> Reviews { get; } = [];
    public ObservableCollection<ReviewThread> Threads { get; } = [];
    public ObservableCollection<ReviewThread> FileThreads { get; } = [];
    public ObservableCollection<string> ReviewFiles { get; } = [];
    public ObservableCollection<ReviewThread> CodeThreads { get; } = [];
    [ObservableProperty] internal string? _selectedReviewFile;
    [ObservableProperty] internal string _fileComposer = "";
    public ObservableCollection<DraftComment> DraftComments { get; } = [];
    public IReadOnlyList<string> Decisions { get; } = ["COMMENT", "APPROVE", "REQUEST_CHANGES"];
    [ObservableProperty] internal string _summary = "";
    [ObservableProperty] internal string _decision = "COMMENT";
    [ObservableProperty] internal string _composer = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ReviewStatus), nameof(HasReviewStatus))]
    internal string _message = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ReviewStatus), nameof(HasReviewStatus), nameof(HasPublicationProblem))]
    internal string? _publicationProblem;
    [ObservableProperty] internal bool _isBusy;
    [ObservableProperty] internal ReviewThread? _selectedThread;
    public ReviewViewModel(IGitHubReader reader, IGitHubWriter writer, IApplicationState store, IGitRepositoryReader git,
        GitHubRepositoryContext context, PullRequest pr, DiffSectionViewModel section, bool isDemo)
    {
        _reviewRecoveryController = new(this);

        _reviewDraftController = new(this);

        _reviewPublicationController = new(this);
        AnchorGit = git;
        Session = new(store, reader);
        _reader = reader;
        _writer = writer;
        _store = store;
        Context = context;
        PullRequest = pr;
        _section = section;
        IsDemo = isDemo;
        _sections.Add(section);
        section.Review = this;
        DraftComments.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ReviewLabel)); foreach (var view in _sections) view.ApplyThreads(); };
    }
    internal string Key => Context.Identity + "/" + PullRequest.Number;
    partial void OnSummaryChanged(string value)
    {
        if (!_loading)
        {
            _draft.Summary = value;
            QueueSave();
        }
    }
    partial void OnComposerChanged(string value)
    {
        OnPropertyChanged(nameof(HasSuggestionPreview));
        if (!_loading && !_switchingComposer)
        {
            _draft.ContextComposers[_composerKey.Encoded] = value;
            if (_composerKey == ComposerKey.Pr)
                _draft.Composer = value;
            QueueSave();
        }
    }
    partial void OnDecisionChanged(string value)
    {
        OnPropertyChanged(nameof(DecisionLabel));
        if (!_loading)
        {
            _draft.Decision = value;
            QueueSave();
        }
    }
    internal void NotifyPublicationState()
    {
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(CanPostFile));
        OnPropertyChanged(nameof(HasPendingPublication));
        OnPropertyChanged(nameof(HasStaleDraft));
        OnPropertyChanged(nameof(IsInitialized));
        OnPropertyChanged(nameof(CanEditDraft));
        OnPropertyChanged(nameof(CanSuggestCodeChange));
        OnPropertyChanged(nameof(SelectedCode));
        OnPropertyChanged(nameof(ReviewStatus));
        OnPropertyChanged(nameof(HasReviewStatus));
    }
    partial void OnIsBusyChanged(bool value) => NotifyPublicationState();
    /// <summary>Restores the account/PR-specific draft, then loads server discussions and current reviewable files.</summary>
    public Task InitializeAsync(IEnumerable<RenderedDiffLine>? selection = null, CancellationToken ct = default) => _reviewDraftController.InitializeAsync(selection, ct);
    public Task FlushAsync() => _reviewDraftController.FlushAsync();
    public void Dispose()
    {
        _discussionLoad?.Cancel();
        CloseInlineEditors();
        foreach (var section in _sections)
            if (ReferenceEquals(section.Review, this))
            {
                section.Review = null;
                section.SetThreads([]);
            }
        _sections.Clear();
    }
}
