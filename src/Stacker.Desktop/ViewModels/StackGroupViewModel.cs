using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class LayerViewModel : ObservableObject
{
    public StackLayerSnapshot Snapshot { get; }
    public StackGroupViewModel? Group { get; internal set; }
    public PullRequest? PullRequest { get; }
    public int Number => Snapshot.Position + 1;
    public string Name => Snapshot.Branch.Replace("refs/heads/", "").Replace("refs/remotes/", "");
    public string Title => PullRequest?.Title ?? Name;
    public string Detail => PullRequest is null ? $"↑{Snapshot.Ahead}  ↓{Snapshot.Behind}" : $"#{PullRequest.Number} · {PullRequest.HeadRef}{(PullRequest.IsDraft ? " · Draft" : "")}";
    public string? Warning => Snapshot.Warning;
    public bool HasWarning => Warning is not null;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isSelected;
    public LayerViewModel(StackLayerSnapshot snapshot, PullRequest? pullRequest = null) { Snapshot = snapshot; PullRequest = pullRequest; }
}
public sealed partial class StackGroupViewModel : ObservableObject
{
    public StackDefinition Definition { get; }
    public ObservableCollection<LayerViewModel> Layers { get; } = [];
    public IReadOnlyList<PullRequest> PullRequests { get; }
    public bool IsRemote => PullRequests.Count > 0;
    public string Name => Definition.Name;
    public string Summary => $"{Layers.Count} {(IsRemote ? "PRs" : "layers")}{(SharedPrefix ? " · shared foundation" : "")}";
    public string BaseLabel => "Base: " + (IsRemote ? PullRequests[0].BaseRef : Definition.Base.Replace("refs/heads/", "").Replace("refs/remotes/", ""));
    public bool SharedPrefix { get; }
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;
    public StackGroupViewModel(StackDefinition definition, IEnumerable<StackLayerSnapshot> layers, IReadOnlyList<PullRequest>? prs = null, bool sharedPrefix = false)
    {
        Definition = definition; PullRequests = prs ?? []; SharedPrefix = sharedPrefix;
        foreach (var layer in layers) Layers.Add(new(layer, layer.Position < PullRequests.Count ? PullRequests[layer.Position] : null) { Group = this });
    }
}
