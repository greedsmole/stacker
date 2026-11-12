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
    public string DecisionLabel { get => Decision switch { "APPROVE" => "Approve", "REQUEST_CHANGES" => "Request changes", _ => "Comment" }; set => Decision = value switch { "Approve" => "APPROVE", "Request changes" => "REQUEST_CHANGES", _ => "COMMENT" }; }
    public IReadOnlyList<string> DecisionLabels { get; } = ["Comment", "Approve", "Request changes"];
    [ObservableProperty] internal int _panelTab;
    partial void OnPanelTabChanged(int value)
    {
        if (value == 1 || value == 2) CloseInlineEditors();
        // The discussion tab always edits the pull-request buffer, never whatever reply was open last.
        if (value == 0) SelectComposer("pr");
    }
    public event Action? DiscussionChanged;
    public void Attach(DiffSectionViewModel section)
    {
        if (section.AssociatedPr?.Number != PullRequest.Number || section.GitHubContext?.Identity != Context.Identity) return;
        _sections.Add(section); section.Review = this; section.SetThreads(Threads);
        if (IsBusy) return;
        if (!ReferenceEquals(_section, section)) CloseInlineEditors();
        _section = section;
        if (section.IsPrSnapshot)
        {
            PullRequest = section.AssociatedPr; RecoverDetachedEditors();
            _reviewableFiles.Clear(); foreach (var file in section.Result.Files) { _reviewableFiles.Add(file.NewPath); if (!ReviewFiles.Contains(file.NewPath)) ReviewFiles.Add(file.NewPath); }
        }
        SelectedReviewFile = section.SelectedFile?.NewPath ?? SelectedReviewFile;
        NotifyPublicationState();
    }
    internal void RecoverDetachedEditors()
=> _reviewRecoveryController.RecoverDetachedEditors();
    public void SelectComposer(string key)
    {
        if (IsBusy) return;
        _switchingComposer = true; _composerKey = ComposerKey.Parse(key).OrThrow();
        Composer = _draft.ContextComposers.GetValueOrDefault(key, ""); _switchingComposer = false;
    }
    public void CloseInlineEditors() { foreach (var view in _sections) foreach (var line in view.Lines) line.Editor = null; }
    public void BeginPrComment() { if (IsBusy) return; CloseInlineEditors(); _selection = null; OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanSuggestCodeChange)); OnPropertyChanged(nameof(SelectedCode)); SelectComposer("pr"); PanelTab = 0; }
    public void BeginReply(ReviewThread thread) { if (IsBusy) return; CloseInlineEditors(); _selection = null; OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanSuggestCodeChange)); OnPropertyChanged(nameof(SelectedCode)); SelectedThread = thread; SelectComposer("reply:" + thread.Id); PanelTab = 2; }
    [RelayCommand]
    internal void RestoreRecoveredText()
    {
        Composer = string.IsNullOrEmpty(Composer) ? RecoveredText : Composer + "\n\n" + RecoveredText;
        _draft.RecoveredText = ""; OnPropertyChanged(nameof(RecoveredText)); OnPropertyChanged(nameof(HasRecoveredText)); QueueSave();
    }
    [RelayCommand]
    internal void EditDraftComment(DraftComment comment)
    {
        if (IsBusy) return;
        CloseInlineEditors(); _selection = comment.Anchor; SelectComposer("line:" + JsonSerializer.Serialize(comment.Anchor));
        Composer = comment.Body; RemoveDraftComment(comment); PanelTab = 3;
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanSuggestCodeChange)); OnPropertyChanged(nameof(SelectedCode)); OnPropertyChanged(nameof(SelectionLabel));
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
    public GitHubRepositoryContext Context { get; }
    public PullRequest PullRequest { get; internal set; }
    public bool IsDemo { get; }
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
        AnchorGit = git; Session = new(store, reader); _reader = reader; _writer = writer; _store = store; Context = context; PullRequest = pr; _section = section; IsDemo = isDemo; _sections.Add(section); section.Review = this;
        DraftComments.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ReviewLabel)); foreach (var view in _sections) view.ApplyThreads(); };
    }
    internal string Key => Context.Identity + "/" + PullRequest.Number;
    partial void OnSummaryChanged(string value) { if (!_loading) { _draft.Summary = value; QueueSave(); } }
    partial void OnComposerChanged(string value) { OnPropertyChanged(nameof(HasSuggestionPreview)); if (!_loading && !_switchingComposer) { _draft.ContextComposers[_composerKey.Encoded] = value; if (_composerKey == ComposerKey.Pr) _draft.Composer = value; QueueSave(); } }
    partial void OnDecisionChanged(string value) { OnPropertyChanged(nameof(DecisionLabel)); if (!_loading) { _draft.Decision = value; QueueSave(); } }
    internal void NotifyPublicationState() { OnPropertyChanged(nameof(CanPublish)); OnPropertyChanged(nameof(CanPostFile)); OnPropertyChanged(nameof(HasPendingPublication)); OnPropertyChanged(nameof(HasStaleDraft)); OnPropertyChanged(nameof(IsInitialized)); OnPropertyChanged(nameof(CanEditDraft)); OnPropertyChanged(nameof(CanSuggestCodeChange)); OnPropertyChanged(nameof(SelectedCode)); OnPropertyChanged(nameof(ReviewStatus)); OnPropertyChanged(nameof(HasReviewStatus)); }
    partial void OnIsBusyChanged(bool value) => NotifyPublicationState();
    partial void OnFileComposerChanged(string value)
    {
        if (!_loading && !_switchingFile && SelectedReviewFile is { } path) { _draft.FileComposers[path] = value; QueueSave(); }
    }
    partial void OnSelectedReviewFileChanged(string? value)
    {
        _switchingFile = true; FileComposer = value is not null && _draft.FileComposers.TryGetValue(value, out var text) ? text : ""; _switchingFile = false;
        UpdateFileThreads();
    }
    internal void UpdateFileThreads()
    {
        Sync(FileThreads, Threads.Where(t => t.IsFileLevel && t.Path == SelectedReviewFile).ToArray(), t => t.Id);
        OnPropertyChanged(nameof(FileThreadsLabel)); OnPropertyChanged(nameof(CanPostFile));
    }
    /// <summary>Restores the account/PR-specific draft, then loads server discussions and current reviewable files.</summary>
    public Task InitializeAsync(IEnumerable<RenderedDiffLine>? selection = null, CancellationToken ct = default) => _reviewDraftController.InitializeAsync(selection, ct);
    /// <summary>Creates a line or contiguous multi-line selection tied to this exact PR diff snapshot.</summary>
    public void SetSelection(IEnumerable<RenderedDiffLine> selected, string? side = null)
    {
        if (IsBusy) return;
        CloseInlineEditors(); var lines = selected.ToArray(); _selection = null; OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanSuggestCodeChange)); OnPropertyChanged(nameof(SelectedCode));
        if (!_section.IsPrSnapshot || lines.Length == 0 || _section.SelectedFile is not { } file) { OnPropertyChanged(nameof(SelectionLabel)); return; }
        if (lines.Any(line => line.IsExpandedContext))
        { Message = "Select lines from the original diff to comment."; NotifyPublicationState(); return; }
        if (!ReviewSelection.TryCreateAnchor(PullRequest, file.NewPath, lines.Select(l => l.Source).ToArray(), side, out _selection, out var error))
        { Message = error!; NotifyPublicationState(); return; }
        SelectComposer("line:" + JsonSerializer.Serialize(_selection));
        OnPropertyChanged(nameof(SelectionLabel)); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanSuggestCodeChange)); OnPropertyChanged(nameof(SelectedCode));
        NotifyPublicationState();
    }
    /// <summary>Prefills a standard GitHub suggestion with the selected head-side code, retaining existing prose.</summary>
    [RelayCommand]
    internal void SuggestCodeChange()
    {
        if (!CanSuggestCodeChange) { Message = "Select current code on the right side to suggest a replacement. Deleted lines support comments only."; return; }
        if (HasSuggestionPreview) { Message = "Edit the existing suggestion block to change the replacement."; return; }
        var suggestion = ReviewContent.Suggestion(SelectedCode!);
        Composer = string.IsNullOrWhiteSpace(Composer) ? suggestion : Composer + "\n\n" + suggestion;
        Message = "Edit the code inside the suggestion block, then add it to your review or post it.";
    }
    internal string? GetSelectedCode()
    {
        if (_selection is not { Side: "RIGHT" } anchor || anchor.HeadSha != _section.Result.HeadSha || anchor.BaseSha != PullRequest.BaseSha || anchor.Path != _section.SelectedFile?.NewPath) return null;
        var start = anchor.StartLine ?? anchor.Line;
        var lines = _section.Lines.Where(l => l.Kind is DiffLineKind.Added or DiffLineKind.Context && l.NewLineNumber >= start && l.NewLineNumber <= anchor.Line).OrderBy(l => l.NewLineNumber).ToArray();
        return lines.Length == anchor.Line - start + 1 ? string.Join("\n", lines.Select(l => l.Text.TrimEnd('\r'))) : null;
    }
    [RelayCommand]
    internal async Task AddToDraftAsync()
    {
        if (IsBusy) return;
        if (_selection is null || string.IsNullOrWhiteSpace(Composer)) { Message = "Select code lines and enter a comment."; return; }
        if (_selection.Side != "RIGHT" && HasSuggestionPreview) { Message = "Suggestions can only replace right-side code. Keep a normal comment on deleted lines."; return; }
        if (_draft.HeadSha != _selection.HeadSha || _draft.BaseSha != _selection.BaseSha) { Message = "This draft belongs to another PR version. Recheck its existing anchors first."; return; }
        var comment = new DraftComment(_selection, Composer); _draft.Comments.Add(comment); DraftComments.Add(comment); Composer = ""; QueueSave(); await _saveQueue; if (_saveError is null) Message = "Line comment saved to the local draft.";
    }
    [RelayCommand] internal void RemoveDraftComment(DraftComment comment) { if (IsBusy) return; _draft.Comments.Remove(comment); DraftComments.Remove(comment); QueueSave(); }
    [RelayCommand]
    internal Task UseCurrentVersionAsync()
=> _reviewRecoveryController.UseCurrentVersionAsync();
    [RelayCommand] internal Task PostCommentAsync() => _reviewPublicationController.PostCommentAsync();
    [RelayCommand] internal Task PostFileCommentAsync() => _reviewPublicationController.PostFileCommentAsync();
    [RelayCommand] internal Task PostInlineAsync() => _reviewPublicationController.PostInlineAsync();
    [RelayCommand] internal Task ReplyAsync() => _reviewPublicationController.ReplyAsync();
    [RelayCommand] internal Task SubmitReviewAsync() => _reviewPublicationController.SubmitReviewAsync();
    internal Task VerifyAsync(IReadOnlyList<ReviewAnchor> anchors) => _reviewPublicationController.VerifyAsync(anchors);
    // Publication first persists a pending marker, then revalidates the server snapshot; a failed write keeps the draft recoverable.
    internal Task PublishAsync(string kind, IReadOnlyList<ReviewAnchor> anchors, Func<string, Task> send, string? filePath = null) => _reviewPublicationController.PublishAsync(kind, anchors, send, filePath);
    [RelayCommand] internal Task ToggleResolvedAsync() => _reviewPublicationController.ToggleResolvedAsync();
    [RelayCommand] internal Task UnlockRetryAsync() => _reviewPublicationController.UnlockRetryAsync();
    internal static string Mark(string body, string marker) => body + "\n\n<!-- stacker:" + marker + " -->";
    // Serialize saves so an older asynchronous write cannot overwrite newer text typed by the user.
    internal void QueueSave() => _reviewDraftController.QueueSave();
    internal Task SaveOrderedAsync(Task previous, ReviewDraft copy) => _reviewDraftController.SaveOrderedAsync(previous, copy);
    /// <summary>Waits for queued draft writes so closing or switching context cannot silently lose edits.</summary>
    public Task FlushAsync() => _reviewDraftController.FlushAsync();
    public void Dispose()
    {
        _discussionLoad?.Cancel(); CloseInlineEditors();
        foreach (var section in _sections)
            if (ReferenceEquals(section.Review, this)) { section.Review = null; section.SetThreads([]); }
        _sections.Clear();
    }
}
