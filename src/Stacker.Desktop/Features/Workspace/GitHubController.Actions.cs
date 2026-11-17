using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class GitHubController
{
    public async Task SaveGitHubPreferencesAsync(RepositoryPreferences preferences)
    {
        if (runtime._repository is null)
            return;
        try
        {
            (await runtime._applicationStore.WriteAsync("repositories", runtime._repository.Root, preferences)).OrThrow();
            owner.GitHubPreferences = preferences;
            runtime._applyingChanges = true;
            try
            {
                await RefreshGitHubAsync();
                BuildDiscoveredGroups();
                if (runtime._activeGroup is not null)
                    await owner.CompareAsync();
            }
            finally { runtime._applyingChanges = false; }
        }
        catch (Exception ex) { owner.Error = ex.Message; }
    }

    public async Task ClearGitHubCacheAsync()
    {
        if (runtime._remoteSnapshot is null || runtime._prObjects is null || owner.IsDemo)
            return;
        try
        {
            runtime._comparison?.Cancel();
            (await runtime._prObjects.ClearAsync(runtime._repository!.Identity!, runtime._remoteSnapshot.Context)).OrThrow();
            owner.DisposeViews();
            owner.Sections.Clear();
            owner.GitHubStatus = "PR retention references cleared. Objects remain managed by the local Git repository.";
        }
        catch (Exception ex) { owner.Error = ex.Message; }
    }

    public async Task SaveRemoteAsLocalAsync()
    {
        if (runtime._activeGroup?.IsRemote != true || runtime._repository is null)
            return;
        try
        {
            var prs = runtime._activeGroup.PullRequests;
            string Resolve(string branch, long repositoryId)
            {
                if (repositoryId != runtime._remoteSnapshot!.Context.RepositoryId)
                    throw new StackerException("Fork refs cannot be mapped to local branches automatically.");
                return new[] { "refs/heads/" + branch, "refs/remotes/origin/" + branch }.FirstOrDefault(r => runtime._repository.Refs.Any(p => p.Name == r)) ?? throw new StackerException($"No local ref for {branch}. Fetch it outside Stacker first.");
            }
            await owner.SaveStackAsync(new(Guid.NewGuid().ToString("N"), runtime._activeGroup.Name, Resolve(prs[0].BaseRef, prs[0].BaseRepositoryId), prs.Select(p => Resolve(p.HeadRef, p.HeadRepositoryId)).ToArray()));
        }
        catch (Exception ex) { owner.Error = ex.Message; }
    }

    public DiffSectionViewModel? CreateDiscussionContext()
    {
        if (owner.SelectedPrForDiscussion is not { } pr || runtime._remoteSnapshot is null || runtime._repository is null)
            return null;
        return new(runtime._git, runtime._repository.Root, new(pr.HeadRef, pr.BaseRef, pr.BaseSha, pr.HeadSha, null, []))
        {
            AssociatedPr = pr,
            GitHubContext = runtime._remoteSnapshot.Context,
            IsDemo = owner.IsDemo
        };
    }

    public async Task OpenPrChangesAsync(DiffSectionViewModel section)
    {
        var pr = section.AssociatedPr;
        if (pr is null || runtime._remoteSnapshot is null)
            return;
        var group = owner.RemoteGroups.Concat(owner.OtherGroups).FirstOrDefault(g => g.PullRequests.Any(p => p.Number == pr.Number));
        if (group is null)
            return;
        var index = group.PullRequests.ToList().FindIndex(p => p.Number == pr.Number);
        await owner.SelectLayerAsync(group.Layers[index], false);
    }
}
