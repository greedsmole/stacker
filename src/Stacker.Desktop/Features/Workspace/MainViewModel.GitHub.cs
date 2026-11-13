// Purpose: GitHub integration for connection status, remote snapshots, discovered PR stacks, and remote object-cache preparation.
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Repository-specific GitHub connection and publication preferences.</summary>
    public RepositoryPreferences GitHubPreferences { get; set; } = new();
    /// <summary>Discovered remote pull-request groups.</summary>
    public ObservableCollection<StackGroupViewModel> RemoteGroups { get; } = [];
    /// <summary>Remote groups that cannot be merged into a shared stack.</summary>
    public ObservableCollection<StackGroupViewModel> OtherGroups { get; } = [];
    /// <summary>Current connection or refresh status.</summary>
    [ObservableProperty] internal string _gitHubStatus = "Not connected";
    /// <summary>True while GitHub data or remote objects are loading.</summary>
    [ObservableProperty] internal bool _gitHubBusy;
    /// <summary>Whether the current workspace is the offline demo and cannot publish.</summary>
    [ObservableProperty] internal bool _isDemo;
    public bool HasRemoteGroups => RemoteGroups.Count > 0;
    public bool HasOtherGroups => OtherGroups.Count > 0;
    [RelayCommand] public Task OpenDemoAsync() => _gitHubController.OpenDemoAsync();
    [RelayCommand] public Task RefreshGitHubAsync() => _gitHubController.RefreshGitHubAsync();
    Task ISyncPresentation.RefreshGitHubAfterPublicationAsync() => _gitHubController.RefreshAfterPublicationAsync();
    public Task RefreshGitHubCoreAsync(CancellationToken ct, bool background) => _gitHubController.RefreshGitHubCoreAsync(ct, background);
    public void BuildDiscoveredGroups() => _gitHubController.BuildDiscoveredGroups();
    /// <summary>Projects pull requests into a display group with synthetic local ref snapshots.</summary>
    /// <param name="id">Stable group identity.</param><param name="name">Display name.</param><param name="prs">Pull requests in layer order.</param><param name="shared">Whether their base is shared.</param>
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
