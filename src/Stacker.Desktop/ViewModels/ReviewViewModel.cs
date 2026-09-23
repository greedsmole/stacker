using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Desktop.ViewModels;

public sealed partial class ReviewViewModel : ObservableObject
{
    private readonly IGitHubReader _reader;
    private readonly IGitHubWriter _writer;
    private readonly ApplicationStore _store;
    private readonly DiffSectionViewModel _section;
    private bool _loading;
    private Task _saveQueue = Task.CompletedTask;
    private string? _saveError;
    private ReviewDraft _draft = new();
    private ReviewAnchor? _selection;
    public GitHubRepositoryContext Context { get; }
    public PullRequest PullRequest { get; private set; }
    public bool IsDemo { get; }
    public bool CanPublish => !IsDemo && !IsBusy && _draft.PendingOperation is null;
    public string Title => PullRequest.Label + (IsDemo ? " · Demo / offline" : "");
    public string SelectionLabel => _selection is null ? "Select code lines in the diff to attach a comment." : $"{_selection.Path} · {_selection.Side} · lines {_selection.StartLine ?? _selection.Line}–{_selection.Line}";
    public ObservableCollection<DiscussionComment> Comments { get; } = [];
    public ObservableCollection<ReviewSummary> Reviews { get; } = [];
    public ObservableCollection<ReviewThread> Threads { get; } = [];
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
    { _reader = reader; _writer = writer; _store = store; Context = context; PullRequest = pr; _section = section; IsDemo = isDemo; }
    private string Key => Context.Identity + "/" + PullRequest.Number;
    partial void OnSummaryChanged(string value) { if (!_loading) { _draft.Summary = value; QueueSave(); } }
    partial void OnComposerChanged(string value) { if (!_loading) { _draft.Composer = value; QueueSave(); } }
    partial void OnDecisionChanged(string value) { if (!_loading) { _draft.Decision = value; QueueSave(); } }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanPublish));
    public async Task InitializeAsync(IEnumerable<RenderedDiffLine>? selection = null)
    {
        _loading = true;
        try
        {
            _draft = await _store.ReadAsync<ReviewDraft>("drafts", Key) ?? new() { ContextIdentity = Context.Identity, PullRequest = PullRequest.Number, BaseSha = PullRequest.BaseSha, HeadSha = PullRequest.HeadSha };
            Summary = _draft.Summary; Decision = _draft.Decision; Composer = _draft.Composer;
            DraftComments.Clear(); foreach (var comment in _draft.Comments) DraftComments.Add(comment);
            if (selection is not null) SetSelection(selection);
            await ReloadAsync();
            if (_draft.HeadSha != PullRequest.HeadSha || _draft.BaseSha != PullRequest.BaseSha) Message = "Draft belongs to an older PR version. Keep its text; recheck/remove line anchors before using the current version.";
        }
        catch (Exception ex) { Message = ex.Message; }
        finally { _loading = false; QueueSave(); }
    }
    public void SetSelection(IEnumerable<RenderedDiffLine> selected)
    {
        var lines = selected.ToArray(); _selection = null;
        if (!_section.IsPrSnapshot || lines.Length == 0 || _section.SelectedFile is not { } file) { OnPropertyChanged(nameof(SelectionLabel)); return; }
        var left = lines.All(l => l.Kind == DiffLineKind.Removed);
        if (!left && lines.Any(l => l.Kind is not (DiffLineKind.Added or DiffLineKind.Context))) { Message = "Select a contiguous range on one side of the diff."; return; }
        var numbers = lines.Select(l => left ? l.OldLineNumber : l.NewLineNumber).Where(n => n.HasValue).Select(n => n!.Value).Distinct().Order().ToArray();
        if (numbers.Length != lines.Length || numbers[^1] - numbers[0] + 1 != numbers.Length) { Message = "Select a contiguous range of code lines."; return; }
        _selection = new(PullRequest.Number, _section.Result.HeadSha, PullRequest.BaseSha, file.NewPath, left ? "LEFT" : "RIGHT", numbers[^1], numbers.Length > 1 ? numbers[0] : null, numbers.Length > 1 ? left ? "LEFT" : "RIGHT" : null);
        OnPropertyChanged(nameof(SelectionLabel));
    }
    [RelayCommand] public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var discussion = IsDemo ? DemoRepositoryGenerator.Discussion(PullRequest) : await _reader.DiscussionAsync(Context, PullRequest);
            Comments.Clear(); foreach (var comment in discussion.Comments) Comments.Add(comment);
            Reviews.Clear(); foreach (var review in discussion.Reviews) Reviews.Add(review);
            Threads.Clear(); foreach (var thread in discussion.Threads) Threads.Add(thread);
            _section.SetThreads(discussion.Threads);
            if (_draft.PendingOperation is { } pending)
            {
                var parts = pending.Split('|'); var found = false;
                if (parts[0] == "resolve") found = discussion.Threads.Any(t => t.Id == parts[1] && t.IsResolved == bool.Parse(parts[2]));
                else
                {
                    var marker = "<!-- stacker:" + parts[1] + " -->";
                    found = discussion.Comments.Any(c => c.Author == Context.User && c.Body.Contains(marker, StringComparison.Ordinal)) || discussion.Reviews.Any(r => r.Author == Context.User && r.Body.Contains(marker, StringComparison.Ordinal)) || discussion.Threads.SelectMany(t => t.Comments).Any(c => c.Author == Context.User && c.Body.Contains(marker, StringComparison.Ordinal));
                }
                if (found) { if (parts[0] == "review") ClearSubmittedDraft(); _draft.PendingOperation = null; Message = "Previous publication confirmed on GitHub."; QueueSave(); }
                else Message = "Publication result is uncertain. Check GitHub before unlocking a retry; no automatic resend will occur.";
            }
            OnPropertyChanged(nameof(CanPublish));
        }
        catch (Exception ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private async Task AddToDraftAsync()
    {
        if (_selection is null || string.IsNullOrWhiteSpace(Composer)) { Message = "Select code lines and enter a comment."; return; }
        if (_draft.HeadSha != _selection.HeadSha || _draft.BaseSha != _selection.BaseSha) { Message = "This draft belongs to another PR version. Recheck its existing anchors first."; return; }
        var comment = new DraftComment(_selection, Composer); _draft.Comments.Add(comment); DraftComments.Add(comment); Composer = ""; QueueSave(); await _saveQueue; if (_saveError is null) Message = "Line comment saved to the local draft.";
    }
    [RelayCommand] private void RemoveDraftComment(DraftComment comment) { _draft.Comments.Remove(comment); DraftComments.Remove(comment); QueueSave(); }
    [RelayCommand] private async Task UseCurrentVersionAsync()
    {
        if (_draft.Comments.Count > 0) { Message = "Remove or recheck the old line comments first. Their text will not be relocated automatically."; return; }
        try
        {
            var current = IsDemo ? PullRequest : await _reader.PullRequestAsync(Context, PullRequest.Number);
            _draft.HeadSha = current.HeadSha; _draft.BaseSha = current.BaseSha; QueueSave(); await FlushAsync(); Message = "Draft summary now targets the current PR. Reopen PR changes to select new lines.";
        }
        catch (Exception ex) { Message = ex.Message; }
    }
    [RelayCommand] private async Task PostCommentAsync() => await PublishAsync("comment", [], async marker => await _writer.CommentAsync(Context, PullRequest.Number, Mark(Composer, marker)));
    [RelayCommand] private async Task PostInlineAsync()
    {
        if (_selection is null) { Message = "Select code lines first."; return; }
        await PublishAsync("inline", [_selection], async marker => await _writer.InlineCommentAsync(Context, new(_selection, Mark(Composer, marker))));
    }
    [RelayCommand] private async Task ReplyAsync()
    {
        if (SelectedThread?.Comments.FirstOrDefault() is not { } first) { Message = "Select a thread to reply to."; return; }
        await PublishAsync("reply", [], async marker => await _writer.ReplyAsync(Context, PullRequest.Number, first.Id, Mark(Composer, marker)));
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
        if (latest.HeadSha != PullRequest.HeadSha || latest.BaseSha != PullRequest.BaseSha) throw new StackerException("PR base/head changed. Refresh GitHub and reopen PR changes. Your draft is preserved.");
        await _reader.ValidateAnchorsAsync(Context, latest, anchors);
    }
    private async Task PublishAsync(string kind, IReadOnlyList<ReviewAnchor> anchors, Func<string, Task> send)
    {
        if (!CanPublish) { Message = IsDemo ? "Demo is offline. Publishing is disabled." : "Resolve the pending publication before sending again."; return; }
        if (kind != "review" && string.IsNullOrWhiteSpace(Composer)) { Message = "Enter a comment first."; return; }
        if (kind == "review" && (_draft.HeadSha != PullRequest.HeadSha || _draft.BaseSha != PullRequest.BaseSha)) { Message = "Draft version differs from this PR snapshot."; return; }
        if (kind == "review" && Decision != "APPROVE" && string.IsNullOrWhiteSpace(Summary) && DraftComments.Count == 0) { Message = "Add a summary or line comments."; return; }
        IsBusy = true;
        try
        {
            await VerifyAsync(anchors);
            var marker = Guid.NewGuid().ToString("N"); _draft.PendingOperation = kind + "|" + marker; QueueSave(); await FlushAsync();
            await send(marker);
            _draft.PendingOperation = null;
            if (kind == "review") ClearSubmittedDraft(); else Composer = "";
            QueueSave(); await FlushAsync(); Message = "Published.";
            await ReloadAsync();
        }
        catch (Exception ex) { Message = ex.Message + (_draft.PendingOperation is null ? "" : " Publication may have reached GitHub. Reload to reconcile before retrying."); }
        finally { IsBusy = false; OnPropertyChanged(nameof(CanPublish)); }
    }
    [RelayCommand] private async Task ToggleResolvedAsync()
    {
        if (!CanPublish || SelectedThread is not { CanResolve: true } thread) return;
        IsBusy = true;
        try
        {
            await VerifyAsync([]); _draft.PendingOperation = $"resolve|{thread.Id}|{!thread.IsResolved}"; QueueSave(); await FlushAsync();
            await _writer.ResolveAsync(Context, thread.Id, !thread.IsResolved);
            _draft.PendingOperation = null; QueueSave(); await FlushAsync(); await ReloadAsync();
        }
        catch (Exception ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private async Task UnlockRetryAsync() { if (IsBusy) return; _draft.PendingOperation = null; QueueSave(); await _saveQueue; OnPropertyChanged(nameof(CanPublish)); if (_saveError is null) Message = "Retry unlocked after your manual GitHub check."; }
    private void ClearSubmittedDraft() { _loading = true; Summary = ""; _loading = false; _draft.Summary = ""; _draft.Comments.Clear(); DraftComments.Clear(); }
    private static string Mark(string body, string marker) => body + "\n\n<!-- stacker:" + marker + " -->";
    private void QueueSave()
    {
        if (_loading) return;
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
