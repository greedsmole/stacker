using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Desktop.ViewModels;

public partial class ReviewViewModel : ObservableObject
{
    private readonly IGitHubReader _reader;
    private readonly IGitHubWriter _writer;
    private readonly ApplicationStore _store;
    private DiffSectionViewModel _section;
    private readonly HashSet<DiffSectionViewModel> _sections = [];
    private string _composerKey = "pr";
    private bool _switchingComposer;
    public string ReviewLabel => $"Review ({DraftComments.Count})";
    public bool HasPendingPublication => _draft.PendingOperation is not null;
    public bool HasStaleDraft => _draft.HeadSha != PullRequest.HeadSha || _draft.BaseSha != PullRequest.BaseSha;
    public bool HasRecoveredText => !string.IsNullOrEmpty(RecoveredText);
    public string RecoveredText => _draft.RecoveredText;
    public bool HasSelection => _selection is not null;
    public string DecisionLabel { get => Decision switch { "APPROVE" => "Approve", "REQUEST_CHANGES" => "Request changes", _ => "Comment" }; set => Decision = value switch { "Approve" => "APPROVE", "Request changes" => "REQUEST_CHANGES", _ => "COMMENT" }; }
    public IReadOnlyList<string> DecisionLabels { get; } = ["Comment", "Approve", "Request changes"];
    [ObservableProperty] private int _panelTab;
    partial void OnPanelTabChanged(int value) { if (value == 1 || value == 2) CloseInlineEditors(); }
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
    private void RecoverDetachedEditors()
    {
        foreach (var (key, text) in _draft.ContextComposers.ToArray())
        {
            if (!key.StartsWith("line:", StringComparison.Ordinal) || string.IsNullOrEmpty(text)) continue;
            ReviewAnchor? anchor;
            try { anchor = JsonSerializer.Deserialize<ReviewAnchor>(key[5..]); } catch (JsonException) { continue; }
            if (anchor is null || anchor.HeadSha == PullRequest.HeadSha && anchor.BaseSha == PullRequest.BaseSha) continue;
            _draft.RecoveredText += (_draft.RecoveredText.Length == 0 ? "" : "\n\n") + $"{anchor.Path}:{anchor.Line} (previous version)\n{text}";
            _draft.ContextComposers.Remove(key);
            if (key == _composerKey) { _selection = null; OnPropertyChanged(nameof(HasSelection)); CloseInlineEditors(); }
        }
        OnPropertyChanged(nameof(RecoveredText)); OnPropertyChanged(nameof(HasRecoveredText)); QueueSave();
    }
    public void SelectComposer(string key)
    {
        if (IsBusy) return;
        _switchingComposer = true; _composerKey = key;
        Composer = _draft.ContextComposers.GetValueOrDefault(key, ""); _switchingComposer = false;
    }
    public void CloseInlineEditors() { foreach (var view in _sections) foreach (var line in view.Lines) line.Editor = null; }
    public void BeginPrComment() { if (IsBusy) return; CloseInlineEditors(); _selection = null; OnPropertyChanged(nameof(HasSelection)); SelectComposer("pr"); PanelTab = 0; }
    public void BeginReply(ReviewThread thread) { if (IsBusy) return; CloseInlineEditors(); _selection = null; OnPropertyChanged(nameof(HasSelection)); SelectedThread = thread; SelectComposer("reply:" + thread.Id); PanelTab = 2; }
    [RelayCommand] private void RestoreRecoveredText()
    {
        Composer = string.IsNullOrEmpty(Composer) ? RecoveredText : Composer + "\n\n" + RecoveredText;
        _draft.RecoveredText = ""; OnPropertyChanged(nameof(RecoveredText)); OnPropertyChanged(nameof(HasRecoveredText)); QueueSave();
    }
    [RelayCommand] private void EditDraftComment(DraftComment comment)
    {
        if (IsBusy) return;
        CloseInlineEditors(); _selection = comment.Anchor; SelectComposer("line:" + JsonSerializer.Serialize(comment.Anchor));
        Composer = comment.Body; RemoveDraftComment(comment); PanelTab = 3;
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(SelectionLabel));
    }
    private bool _loading;
    private bool _draftLoaded;
    public bool IsInitialized => _draftLoaded;
    public bool CanEditDraft => _draftLoaded && !IsBusy;
    [RelayCommand] private Task RetryInitializeAsync() => InitializeAsync();
    private bool _backgroundRead;
    private CancellationTokenSource? _discussionLoad;
    public void Detach(DiffSectionViewModel section) => _sections.Remove(section);
    private bool _switchingFile;
    private readonly HashSet<string> _reviewableFiles = new(StringComparer.Ordinal);
    private Task _saveQueue = Task.CompletedTask;
    private string? _saveError;
    private ReviewDraft _draft = new();
    private ReviewAnchor? _selection;
    public GitHubRepositoryContext Context { get; }
    public PullRequest PullRequest { get; private set; }
    public bool IsDemo { get; }
    public bool CanPublish => _draftLoaded && !IsDemo && !IsBusy && _draft.PendingOperation is null;
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
    [ObservableProperty] private string? _selectedReviewFile;
    [ObservableProperty] private string _fileComposer = "";
    public ObservableCollection<DraftComment> DraftComments { get; } = [];
    public IReadOnlyList<string> Decisions { get; } = ["COMMENT", "APPROVE", "REQUEST_CHANGES"];
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _decision = "COMMENT";
    [ObservableProperty] private string _composer = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ReviewThread? _selectedThread;
    public ReviewViewModel(IGitHubReader reader, IGitHubWriter writer, ApplicationStore store, IGitRepositoryReader git,
        GitHubRepositoryContext context, PullRequest pr, DiffSectionViewModel section, bool isDemo)
    { _reader = reader; _writer = writer; _store = store; Context = context; PullRequest = pr; _section = section; IsDemo = isDemo; _sections.Add(section); section.Review = this;
        DraftComments.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ReviewLabel)); foreach (var view in _sections) view.ApplyThreads(); };
    }
    private string Key => Context.Identity + "/" + PullRequest.Number;
    partial void OnSummaryChanged(string value) { if (!_loading) { _draft.Summary = value; QueueSave(); } }
    partial void OnComposerChanged(string value) { if (!_loading && !_switchingComposer) { _draft.ContextComposers[_composerKey] = value; if (_composerKey == "pr") _draft.Composer = value; QueueSave(); } }
    partial void OnDecisionChanged(string value) { OnPropertyChanged(nameof(DecisionLabel)); if (!_loading) { _draft.Decision = value; QueueSave(); } }
    private void NotifyPublicationState() { OnPropertyChanged(nameof(CanPublish)); OnPropertyChanged(nameof(CanPostFile)); OnPropertyChanged(nameof(HasPendingPublication)); OnPropertyChanged(nameof(HasStaleDraft)); OnPropertyChanged(nameof(IsInitialized)); OnPropertyChanged(nameof(CanEditDraft)); }
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
    private void UpdateFileThreads()
    {
        Sync(FileThreads, Threads.Where(t => t.IsFileLevel && t.Path == SelectedReviewFile).ToArray(), t => t.Id);
        OnPropertyChanged(nameof(FileThreadsLabel)); OnPropertyChanged(nameof(CanPostFile));
    }
    public async Task InitializeAsync(IEnumerable<RenderedDiffLine>? selection = null, CancellationToken ct = default)
    {
        _loading = true;
        try
        {
            _draft = await _store.ReadAsync<ReviewDraft>("drafts", Key, ct) ?? new() { ContextIdentity = Context.Identity, PullRequest = PullRequest.Number, BaseSha = PullRequest.BaseSha, HeadSha = PullRequest.HeadSha, EditorVersion = 1 };
            _draftLoaded = true;
            if (_draft.EditorVersion == 0) { _draft.RecoveredText = _draft.Composer; _draft.Composer = ""; _draft.EditorVersion = 1; }
            RecoverDetachedEditors();
            Summary = _draft.Summary; Decision = _draft.Decision; Composer = _draft.ContextComposers.GetValueOrDefault("pr", _draft.Composer);
            OnPropertyChanged(nameof(RecoveredText)); OnPropertyChanged(nameof(HasRecoveredText));
            _draft.FileComposers ??= [];
            IReadOnlyList<string> files = _section.IsPrSnapshot ? _section.Result.Files.Select(f => f.NewPath).ToArray()
                : IsDemo ? [] : await _reader.ChangedFilePathsAsync(Context, PullRequest, ct);
            foreach (var file in files) { ReviewFiles.Add(file); _reviewableFiles.Add(file); }
            SelectedReviewFile = _section.SelectedFile?.NewPath ?? ReviewFiles.FirstOrDefault();
            DraftComments.Clear(); foreach (var comment in _draft.Comments) DraftComments.Add(comment);
            if (selection is not null) SetSelection(selection);
            await ReloadAsync(ct);
            if (_draft.HeadSha != PullRequest.HeadSha || _draft.BaseSha != PullRequest.BaseSha) Message = "Draft belongs to an older PR version. Keep its text; recheck/remove line anchors before using the current version.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Message = ex.Message; }
        finally { _loading = false; NotifyPublicationState(); QueueSave(); }
    }
    public void SetSelection(IEnumerable<RenderedDiffLine> selected, string? side = null)
    {
        if (IsBusy) return;
        CloseInlineEditors(); var lines = selected.ToArray(); _selection = null; OnPropertyChanged(nameof(HasSelection));
        if (!_section.IsPrSnapshot || lines.Length == 0 || _section.SelectedFile is not { } file) { OnPropertyChanged(nameof(SelectionLabel)); return; }
        var left = side == "LEFT" || side is null && lines.All(l => l.Kind == DiffLineKind.Removed);
        if (left ? lines.Any(l => l.Kind is not (DiffLineKind.Removed or DiffLineKind.Context)) : lines.Any(l => l.Kind is not (DiffLineKind.Added or DiffLineKind.Context))) { Message = "Select a contiguous range on one side of the diff."; return; }
        var numbers = lines.Select(l => left ? l.OldLineNumber : l.NewLineNumber).Where(n => n.HasValue).Select(n => n!.Value).Distinct().Order().ToArray();
        if (numbers.Length != lines.Length || numbers[^1] - numbers[0] + 1 != numbers.Length) { Message = "Select a contiguous range of code lines."; return; }
        _selection = new(PullRequest.Number, _section.Result.HeadSha, PullRequest.BaseSha, file.NewPath, left ? "LEFT" : "RIGHT", numbers[^1], numbers.Length > 1 ? numbers[0] : null, numbers.Length > 1 ? left ? "LEFT" : "RIGHT" : null);
        SelectComposer("line:" + JsonSerializer.Serialize(_selection));
        OnPropertyChanged(nameof(SelectionLabel)); OnPropertyChanged(nameof(HasSelection));
    }
    [RelayCommand] public async Task ReloadAsync(CancellationToken ct = default)
    {
        _discussionLoad?.Cancel();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(ct); _discussionLoad = load;
        var snapshot = PullRequest;
        try
        {
            var discussion = IsDemo ? DemoRepositoryGenerator.Discussion(PullRequest) : await _reader.DiscussionAsync(Context, snapshot, load.Token);
            load.Token.ThrowIfCancellationRequested();
            if (snapshot != PullRequest) return;
            Sync(Comments, discussion.Comments, c => c.Id); Sync(Reviews, discussion.Reviews, r => r.Id);
            var selectedId = SelectedThread?.Id;
            Sync(Threads, discussion.Threads, t => t.Id);
            SelectedThread = Threads.FirstOrDefault(t => t.Id == selectedId);
            foreach (var view in _sections) view.SetThreads(Threads);
            Sync(CodeThreads, Threads.Where(t => !t.IsFileLevel).ToArray(), t => t.Id);
            DiscussionChanged?.Invoke();
            foreach (var path in discussion.Threads.Select(t => t.Path).OfType<string>().Concat(_draft.FileComposers.Keys).Distinct())
                if (!ReviewFiles.Contains(path)) ReviewFiles.Add(path);
            UpdateFileThreads();
            if (_draft.PendingOperation is { } pending)
            {
                var parts = pending.Split('|'); var found = false;
                if (parts[0] == "resolve") found = discussion.Threads.Any(t => t.Id == parts[1] && t.IsResolved == bool.Parse(parts[2]));
                else
                {
                    var marker = "<!-- stacker:" + parts[1] + " -->";
                    found = discussion.Comments.Any(c => c.Author == Context.User && c.Body.Contains(marker, StringComparison.Ordinal)) || discussion.Reviews.Any(r => r.Author == Context.User && r.Body.Contains(marker, StringComparison.Ordinal)) || discussion.Threads.SelectMany(t => t.Comments).Any(c => c.Author == Context.User && c.Body.Contains(marker, StringComparison.Ordinal));
                }
                if (found) { if (parts[0] == "review") ClearSubmittedDraft();
                    else if (parts[0] == "file" && _draft.PendingFilePath is { } path) ClearPublishedFile(path);
                    else if (parts[0] is "comment" or "inline" or "reply") ClearPublishedComposer();
                    _draft.PendingOperation = null; _draft.PendingFilePath = null; Message = "Previous publication confirmed on GitHub."; QueueSave(); }
                else Message = "Publication result is uncertain. Check GitHub before unlocking a retry; no automatic resend will occur.";
            }
            NotifyPublicationState();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception ex) { Message = ex.Message; if (_backgroundRead) throw; }
        finally { if (ReferenceEquals(_discussionLoad, load)) _discussionLoad = null; }
    }
    public async Task RefreshDiscussionAsync(CancellationToken ct = default)
    { if (IsBusy) return; _backgroundRead = true; try { await ReloadAsync(ct); } finally { _backgroundRead = false; } }
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> incoming, Func<T, string> key)
    {
        for (var i = target.Count - 1; i >= 0; i--) if (!incoming.Any(v => key(v) == key(target[i]))) target.RemoveAt(i);
        for (var i = 0; i < incoming.Count; i++)
        {
            var old = target.FirstOrDefault(v => key(v) == key(incoming[i]));
            if (old is null) target.Insert(i, incoming[i]);
            else { var index = target.IndexOf(old); if (index != i) target.Move(index, i); if (JsonSerializer.Serialize(old) != JsonSerializer.Serialize(incoming[i])) target[i] = incoming[i]; }
        }
    }
    private void ClearPublishedComposer()
    {
        var key = _draft.PendingComposerKey ?? "pr";
        if (_draft.ContextComposers.GetValueOrDefault(key) == _draft.PendingBody || _draft.PendingBody is null)
        { _draft.ContextComposers[key] = ""; if (key == "pr") _draft.Composer = ""; if (key == _composerKey) Composer = ""; }
        _draft.PendingComposerKey = null; _draft.PendingBody = null;
    }
    [RelayCommand] private async Task AddToDraftAsync()
    {
        if (IsBusy) return;
        if (_selection is null || string.IsNullOrWhiteSpace(Composer)) { Message = "Select code lines and enter a comment."; return; }
        if (_draft.HeadSha != _selection.HeadSha || _draft.BaseSha != _selection.BaseSha) { Message = "This draft belongs to another PR version. Recheck its existing anchors first."; return; }
        var comment = new DraftComment(_selection, Composer); _draft.Comments.Add(comment); DraftComments.Add(comment); Composer = ""; QueueSave(); await _saveQueue; if (_saveError is null) Message = "Line comment saved to the local draft.";
    }
    [RelayCommand] private void RemoveDraftComment(DraftComment comment) { if (IsBusy) return; _draft.Comments.Remove(comment); DraftComments.Remove(comment); QueueSave(); }
    [RelayCommand] private async Task UseCurrentVersionAsync()
    {
        if (_draft.Comments.Count > 0) { Message = "Remove or recheck the old line comments first. Their text will not be relocated automatically."; return; }
        try
        {
            var current = IsDemo ? PullRequest : await _reader.PullRequestAsync(Context, PullRequest.Number);
            if (current.HeadSha != PullRequest.HeadSha || current.BaseSha != PullRequest.BaseSha) { Message = "Apply Update changes before rechecking this draft."; return; }
            _draft.HeadSha = current.HeadSha; _draft.BaseSha = current.BaseSha; NotifyPublicationState(); QueueSave(); await FlushAsync(); Message = "Draft text now targets the current PR. Reopen PR changes to select new lines or files.";
        }
        catch (Exception ex) { Message = ex.Message; }
    }
    [RelayCommand] private async Task PostCommentAsync()
    { var body = Composer; await PublishAsync("comment", [], marker => _writer.CommentAsync(Context, PullRequest.Number, Mark(body, marker))); }
    [RelayCommand] private async Task PostFileCommentAsync()
    {
        if (!CanPostFile) { Message = IsDemo ? "Demo is offline. Publishing is disabled." : "Select a current PR file before commenting. Reload PR changes if it is no longer available."; return; }
        if (_draft.HeadSha != PullRequest.HeadSha || _draft.BaseSha != PullRequest.BaseSha) { Message = "File draft belongs to an older PR version. Recheck it before adopting the current version."; return; }
        var path = SelectedReviewFile!; var body = FileComposer;
        await PublishAsync("file", [], marker => _writer.FileCommentAsync(Context, PullRequest, path, Mark(body, marker)), path);
    }
    [RelayCommand] private async Task PostInlineAsync()
    {
        if (_selection is null) { Message = "Select code lines first."; return; }
        var anchor = _selection; var body = Composer;
        await PublishAsync("inline", [anchor], marker => _writer.InlineCommentAsync(Context, new(anchor, Mark(body, marker))));
    }
    [RelayCommand] private async Task ReplyAsync()
    {
        if (SelectedThread?.Comments.FirstOrDefault() is not { } first) { Message = "Select a thread to reply to."; return; }
        var body = Composer;
        await PublishAsync("reply", [], marker => _writer.ReplyAsync(Context, PullRequest.Number, first.Id, Mark(body, marker)));
    }
    [RelayCommand] private async Task SubmitReviewAsync() => await PublishAsync("review", _draft.Comments.Select(c => c.Anchor).ToArray(), async marker =>
    {
        var copy = JsonSerializer.Deserialize<ReviewDraft>(JsonSerializer.Serialize(_draft))!; copy.Summary = Mark(copy.Summary, marker);
        await _writer.SubmitReviewAsync(Context, copy);
    });
    private async Task VerifyAsync(IReadOnlyList<ReviewAnchor> anchors)
    {
        await _reader.VerifyAccessAsync(Context);
        var latest = await _reader.PullRequestAsync(Context, PullRequest.Number);
        if (latest.HeadSha != PullRequest.HeadSha || latest.BaseSha != PullRequest.BaseSha) throw new StackerException("PR base/head changed. Refresh and reopen PR changes. Your draft is preserved.");
        await _reader.ValidateAnchorsAsync(Context, latest, anchors);
    }
    private async Task PublishAsync(string kind, IReadOnlyList<ReviewAnchor> anchors, Func<string, Task> send, string? filePath = null)
    {
        if (!CanPublish) { Message = IsDemo ? "Demo is offline. Publishing is disabled." : "Resolve the pending publication before sending again."; return; }
        if (kind != "review" && string.IsNullOrWhiteSpace(kind == "file" ? FileComposer : Composer)) { Message = "Enter a comment first."; return; }
        if (kind == "review" && (_draft.HeadSha != PullRequest.HeadSha || _draft.BaseSha != PullRequest.BaseSha)) { Message = "Draft version differs from this PR snapshot."; return; }
        if (kind == "review" && Decision == "REQUEST_CHANGES" && string.IsNullOrWhiteSpace(Summary)) { Message = "Explain the requested changes in the review summary."; return; }
        if (kind == "review" && Decision != "APPROVE" && string.IsNullOrWhiteSpace(Summary) && DraftComments.Count == 0) { Message = "Add a summary or line comments."; return; }
        _discussionLoad?.Cancel(); IsBusy = true;
        try
        {
            await VerifyAsync(anchors);
            if (filePath is not null) await _reader.ValidateFileAsync(Context, PullRequest, filePath);
            var marker = Guid.NewGuid().ToString("N"); _draft.PendingOperation = kind + "|" + marker; _draft.PendingFilePath = filePath; _draft.PendingComposerKey = _composerKey; _draft.PendingBody = kind == "file" ? FileComposer : Composer;
            if (kind == "review") { _draft.PendingSummary = Summary; _draft.PendingReviewComments = _draft.Comments.ToList(); }
            QueueSave(); await FlushAsync();
            await send(marker);
            _draft.PendingOperation = null; _draft.PendingFilePath = null;
            if (kind == "review") ClearSubmittedDraft(); else if (kind == "file" && filePath is not null) ClearPublishedFile(filePath); else ClearPublishedComposer();
            QueueSave(); await FlushAsync(); Message = "Published.";
            await ReloadAsync();
        }
        catch (Exception ex) { Message = ex.Message + (_draft.PendingOperation is null ? "" : " Publication may have reached GitHub. Reload to reconcile before retrying."); }
        finally { IsBusy = false; NotifyPublicationState(); }
    }
    [RelayCommand] private async Task ToggleResolvedAsync()
    {
        if (!CanPublish || SelectedThread is not { CanResolve: true } thread) return;
        _discussionLoad?.Cancel(); IsBusy = true;
        try
        {
            await VerifyAsync([]); _draft.PendingOperation = $"resolve|{thread.Id}|{!thread.IsResolved}"; QueueSave(); await FlushAsync();
            await _writer.ResolveAsync(Context, thread.Id, !thread.IsResolved);
            _draft.PendingOperation = null; QueueSave(); await FlushAsync(); await ReloadAsync();
        }
        catch (Exception ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private async Task UnlockRetryAsync() { if (IsBusy) return; _draft.PendingOperation = null; _draft.PendingFilePath = null; QueueSave(); await _saveQueue; NotifyPublicationState(); if (_saveError is null) Message = "Retry unlocked after your manual GitHub check."; }
    private void ClearPublishedFile(string path)
    {
        if (_draft.PendingBody is null || _draft.FileComposers.GetValueOrDefault(path) == _draft.PendingBody)
        { _draft.FileComposers[path] = ""; if (SelectedReviewFile == path) FileComposer = ""; }
        _draft.PendingBody = null;
    }
    private void ClearSubmittedDraft()
    {
        if (_draft.PendingReviewComments is null) return; // Older uncertain operations have no safe snapshot to remove.
        if (Summary == _draft.PendingSummary) { _loading = true; Summary = ""; _loading = false; _draft.Summary = ""; }
        foreach (var sent in _draft.PendingReviewComments) { _draft.Comments.Remove(sent); DraftComments.Remove(sent); }
        _draft.PendingReviewComments = null; _draft.PendingSummary = null;
    }
    private static string Mark(string body, string marker) => body + "\n\n<!-- stacker:" + marker + " -->";
    private void QueueSave()
    {
        if (_loading || !_draftLoaded) return;
        var copy = JsonSerializer.Deserialize<ReviewDraft>(JsonSerializer.Serialize(_draft))!;
        _saveQueue = SaveOrderedAsync(_saveQueue, copy);
    }
    private async Task SaveOrderedAsync(Task previous, ReviewDraft copy)
    {
        await previous;
        try { await _store.WriteAsync("drafts", Key, copy); _saveError = null; }
        catch (Exception ex) { _saveError = "Cannot save draft: " + ex.Message; Message = _saveError; }
    }
    public async Task FlushAsync() { await _saveQueue; if (_saveError is not null) throw new StackerException(_saveError); }
}
