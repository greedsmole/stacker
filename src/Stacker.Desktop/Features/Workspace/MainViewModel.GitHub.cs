using Stacker.Core.Framework;
// Purpose: GitHub integration for connection status, remote snapshots, discovered PR stacks, and remote object-cache preparation.
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public RepositoryPreferences GitHubPreferences { get; set; } = new();
    public ObservableCollection<StackGroupViewModel> RemoteGroups { get; } = [];
    public ObservableCollection<StackGroupViewModel> OtherGroups { get; } = [];
    [ObservableProperty] internal string _gitHubStatus = "Not connected";
    [ObservableProperty] internal bool _gitHubBusy;
    [ObservableProperty] internal bool _isDemo;
    public bool HasRemoteGroups => RemoteGroups.Count > 0;
    public bool HasOtherGroups => OtherGroups.Count > 0;
    [RelayCommand] public Task OpenDemoAsync() => _gitHubController.OpenDemoAsync();
    [RelayCommand] public Task RefreshGitHubAsync() => _gitHubController.RefreshGitHubAsync();
    public Task RefreshGitHubCoreAsync(CancellationToken ct, bool background) => _gitHubController.RefreshGitHubCoreAsync(ct, background);
    public void BuildDiscoveredGroups() => _gitHubController.BuildDiscoveredGroups();
    internal static StackGroupViewModel RemoteGroup(string id, string name, IReadOnlyList<PullRequest> prs, bool shared)
    {
        var definition = new StackDefinition(id, name, RemoteRefs.BaseRef(prs[0]), prs.Select(RemoteRefs.HeadRef).ToArray());
        var layers = prs.Select((p, i) => new StackLayerSnapshot(i, "refs/heads/" + p.HeadRef, "refs/heads/" + p.BaseRef, p.HeadSha, p.BaseSha, 0, 0, null));
        return new(definition, layers, prs, shared);
    }
    public PullRequest? FindLocalPr(string branch) => _gitHubController.FindLocalPr(branch);
    public Task<RepositorySnapshot> PrepareRemoteAsync(StackGroupViewModel group, DiffMode mode, int[] positions, CancellationToken ct) => _gitHubController.PrepareRemoteAsync(group, mode, positions, ct);
    public Task SaveGitHubPreferencesAsync(RepositoryPreferences preferences) => _gitHubController.SaveGitHubPreferencesAsync(preferences);
    public Task ClearGitHubCacheAsync() => _gitHubController.ClearGitHubCacheAsync();
    public Task SaveRemoteAsLocalAsync() => _gitHubController.SaveRemoteAsLocalAsync();
    public PullRequest? SelectedPrForDiscussion => _runtime._selectedPositions.Length != 1 || _runtime._activeGroup is null ? null
        : _runtime._activeGroup.IsRemote ? _runtime._activeGroup.PullRequests.ElementAtOrDefault(_runtime._selectedPositions[0])
        : FindLocalPr(_runtime._activeGroup.Layers.ElementAtOrDefault(_runtime._selectedPositions[0])?.Snapshot.Branch ?? "");
    public bool HasPrDiscussion => _runtime._remoteSnapshot is not null && SelectedPrForDiscussion is not null;
    public DiffSectionViewModel? CreateDiscussionContext() => _gitHubController.CreateDiscussionContext();
    public Task OpenPrChangesAsync(DiffSectionViewModel section) => _gitHubController.OpenPrChangesAsync(section);
}
