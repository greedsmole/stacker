using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed class ReviewRecoveryController(ReviewViewModel owner)
{
    public void RecoverDetachedEditors()
    {
        foreach (var (key, text) in owner._draft.ContextComposers.ToArray())
        {
            if (!key.StartsWith("line:", StringComparison.Ordinal) || string.IsNullOrEmpty(text)) continue;
            ReviewAnchor? anchor;
            try { anchor = JsonSerializer.Deserialize<ReviewAnchor>(key[5..]); } catch (JsonException) { continue; }
            if (anchor is null || anchor.HeadSha == owner.PullRequest.HeadSha && anchor.BaseSha == owner.PullRequest.BaseSha) continue;
            owner._draft.RecoveredText += (owner._draft.RecoveredText.Length == 0 ? "" : "\n\n") + $"{anchor.Path}:{anchor.Line} (previous version)\n{text}";
            owner._draft.ContextComposers.Remove(key);
            if (key == owner._composerKey.Encoded) { owner._selection = null; owner.Notify(nameof(owner.HasSelection)); owner.Notify(nameof(owner.CanSuggestCodeChange)); owner.Notify(nameof(owner.SelectedCode)); owner.CloseInlineEditors(); }
        }
        owner.Notify(nameof(owner.RecoveredText)); owner.Notify(nameof(owner.HasRecoveredText)); owner.QueueSave();
    }

    public async Task UseCurrentVersionAsync()
    {
        if (owner._draft.Comments.Count > 0) { owner.Message = "Remove or recheck the old line comments first. Their text will not be relocated automatically."; return; }
        try
        {
            var current = owner.IsDemo ? owner.PullRequest : (await owner._reader.PullRequestAsync(owner.Context, owner.PullRequest.Number)).OrThrow();
            if (current.HeadSha != owner.PullRequest.HeadSha || current.BaseSha != owner.PullRequest.BaseSha) { owner.Message = "Apply Update changes before rechecking this draft."; return; }
            owner._draft.HeadSha = current.HeadSha; owner._draft.BaseSha = current.BaseSha; owner.NotifyPublicationState(); owner.QueueSave(); await owner.FlushAsync(); owner.Message = "Draft text now targets the current PR. Reopen PR changes to select new lines or files.";
        }
        catch (Exception ex) { owner.Message = ex.Message; }
    }

}
