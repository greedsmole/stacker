using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public partial class ReviewViewModel
{
    public string ExportRecoveryText() => System.Text.Json.JsonSerializer.Serialize(_draft, new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
    public async Task RetrySaveAsync(){QueueSave();await FlushAsync();}
    internal IGitRepositoryReader AnchorGit = null!;
    public IHistoryGit? HistoryObjects { get; set; }
    public RepositoryIdentity? HistoryRepository { get; set; }
    private DraftComment? _proposedComment;
    private AnchorProposal? _proposal;
    [ObservableProperty] private string _anchorPreview = "";
    public bool HasAnchorProposal => _proposal is not null;
    public async Task ProposeReanchorAsync(DraftComment comment)
    {
        if (IsBusy || HasPendingPublication) return;
        _proposal = null; _proposedComment = null; AnchorPreview = ""; OnPropertyChanged(nameof(HasAnchorProposal));
        if (HistoryObjects is null || HistoryRepository is null || AnchorGit is not IBlobReader blobs) { Message = "Historical code is unavailable; select new lines manually."; return; }
        if (comment.Anchor.HeadSha == PullRequest.HeadSha && comment.Anchor.BaseSha == PullRequest.BaseSha) { Message = "This comment already targets the current version."; return; }
        IsBusy = true;
        try
        {
            using var lease=(await HistoryObjects.AcquireAsync(HistoryRepository,Context.Identity)).OrThrow();
            var root=lease.Root;
            var anchor = comment.Anchor;
            var candidates = _section.Result.Files.Where(f => f.NewPath == anchor.Path || f.OldPath == anchor.Path).ToArray();
            if (candidates.Length == 0)
            {
                var transitions = (await AnchorGit.FilesAsync(root, anchor.HeadSha, PullRequest.HeadSha)).OrThrow();
                var renamed = transitions.Where(f => f.Kind == FileChangeKind.Renamed && f.OldPath == anchor.Path).ToArray();
                if (renamed.Length == 1) candidates = _section.Result.Files.Where(f => f.NewPath == renamed[0].NewPath).ToArray();
            }
            if (candidates.Length != 1) { Message = "No unique current file. Select a new destination manually."; return; }
            var file = candidates[0];
            var oldCommit = anchor.HeadSha; var oldPath = anchor.Path;
            var newCommit = PullRequest.HeadSha;
            if (anchor.Side == "LEFT")
            {
                var oldBases = (await AnchorGit.MergeBasesAsync(root, anchor.BaseSha, anchor.HeadSha)).OrThrow();
                if (oldBases.Count != 1) { Message = "Old base is ambiguous. Choose a new destination manually."; return; }
                oldCommit = oldBases[0]; newCommit = _section.Result.BaseSha;
                var oldFiles = (await AnchorGit.FilesAsync(root, oldCommit, anchor.HeadSha)).OrThrow();
                var oldFile = oldFiles.SingleOrDefault(f => f.NewPath == anchor.Path);
                if (oldFile is null) { Message = "Original left-side file is unavailable."; return; }
                oldPath = oldFile.OldPath;
            }
            var old = (await blobs.ReadBlobAsync(root, oldCommit, oldPath)).OrThrow();
            var current = (await blobs.ReadBlobAsync(_section.Root, newCommit, anchor.Side == "RIGHT" ? file.NewPath : file.OldPath)).OrThrow();
            if (old is null || current is null) { Message = "Original text is unavailable. Keep the comment and select new lines manually."; return; }
            _proposal = AnchorMapping.Propose(anchor, PullRequest, file.NewPath, old.Text, current.Text);
            if (_proposal is null) { Message = "Selected text and context have no unique match. Select new lines manually."; return; }
            (await _reader.ValidateAnchorsAsync(Context, PullRequest, [_proposal.Proposed])).OrThrow();
            _proposedComment = comment;
            AnchorPreview = $"OLD {anchor.Path}:{anchor.StartLine ?? anchor.Line}–{anchor.Line}\n{_proposal.OldContext}\n\nNEW {_proposal.Proposed.Path}:{_proposal.Proposed.StartLine ?? _proposal.Proposed.Line}–{_proposal.Proposed.Line}\n{_proposal.NewContext}";
            Message = "Inspect both contexts, then confirm the new anchor. Nothing has been published.";
        }
        catch (Exception ex) { _proposal = null; Message = ex.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasAnchorProposal)); }
    }
    [RelayCommand] private async Task ConfirmReanchorAsync()
    {
        if (IsBusy || HasPendingPublication || _proposal is not { } proposal || _proposedComment is not { } old) return;
        if (proposal.Proposed.HeadSha != PullRequest.HeadSha || proposal.Proposed.BaseSha != PullRequest.BaseSha) { Message = "The proposal is stale. Generate it again."; return; }
        var index = _draft.Comments.IndexOf(old);
        if (index < 0) return;
        var replacement = old with { Anchor = proposal.Proposed };
        _draft.PreviousAnchors.Add(old); _draft.Comments[index] = replacement;
        DraftComments[DraftComments.IndexOf(old)] = replacement;
        if (_draft.Comments.All(c => c.Anchor.HeadSha == PullRequest.HeadSha && c.Anchor.BaseSha == PullRequest.BaseSha))
        { _draft.HeadSha = PullRequest.HeadSha; _draft.BaseSha = PullRequest.BaseSha; }
        _proposal = null; _proposedComment = null; AnchorPreview = ""; OnPropertyChanged(nameof(HasAnchorProposal));
        NotifyPublicationState(); QueueSave(); await FlushAsync(); Message = "New anchor saved locally. Original anchor retained in history.";
    }
}
