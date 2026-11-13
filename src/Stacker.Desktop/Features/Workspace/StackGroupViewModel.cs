// Purpose: Presentation state for one stack and its numbered layers, including associated PR metadata and expansion/selection state.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;
/// <summary>Presentation state for a local or remote stack group.</summary>
public sealed partial class StackGroupViewModel : ObservableObject
{
    /// <summary>Definition supplying stable identity, name and base.</summary>
    public StackDefinition Definition
    {
        get;
    }
    /// <summary>Layers in bottom-up stack order.</summary>
    public ObservableCollection<LayerViewModel> Layers { get; } = [];
    /// <summary>Pull requests associated with remote layers.</summary>
    public IReadOnlyList<PullRequest> PullRequests
    {
        get;
    }
    public bool IsRemote => PullRequests.Count > 0;
    public bool IsSinglePr => IsRemote && PullRequests.Count == 1;
    public bool IsStack => !IsSinglePr;
    public bool ShowLayers => IsStack && IsExpanded;
    public string Name => Definition.Name;
    public string Summary => IsSinglePr ? $"#{PullRequests[0].Number} · {PullRequests[0].BaseRef} ← {PullRequests[0].HeadRef}"
        : $"{Layers.Count} {(IsRemote ? "PRs" : "layers")}{(SharedPrefix ? " · shared foundation" : "")}";
    public string BaseLabel => "Base: " + (IsRemote ? PullRequests[0].BaseRef : Definition.Base.Replace("refs/heads/", "").Replace("refs/remotes/", ""));
    /// <summary>Whether remote pull requests share a common foundation.</summary>
    public bool SharedPrefix
    {
        get;
    }
    /// <summary>True when this stack's last restack can still be undone.</summary>
    [ObservableProperty] private bool _undoAvailable;
    /// <summary>Whether the layer list is expanded.</summary>
    [ObservableProperty] private bool _isExpanded = true;
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowLayers));
    /// <summary>Whether this group is selected.</summary>
    [ObservableProperty] private bool _isSelected;
    /// <summary>Creates a group and assigns each layer to it.</summary>
    public StackGroupViewModel(StackDefinition definition, IEnumerable<StackLayerSnapshot> layers, IReadOnlyList<PullRequest>? prs = null, bool sharedPrefix = false)
    {
        Definition = definition;
        PullRequests = prs ?? [];
        SharedPrefix = sharedPrefix;
        foreach (var layer in layers)
            Layers.Add(new(layer, layer.Position < PullRequests.Count ? PullRequests[layer.Position] : null)
            {
                Group = this
            });
    }
}
