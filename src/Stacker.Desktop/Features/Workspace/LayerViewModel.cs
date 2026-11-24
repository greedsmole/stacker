using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

/// <summary>Presentation state for one numbered stack layer and optional pull request.</summary>
public sealed partial class LayerViewModel : ObservableObject
{
    /// <summary>Immutable layer snapshot used to derive display values.</summary>
    public StackLayerSnapshot Snapshot
    {
        get;
    }
    /// <summary>Containing group, assigned after construction.</summary>
    public StackGroupViewModel? Group
    {
        get; internal set;
    }
    /// <summary>Associated remote pull request, when present.</summary>
    public PullRequest? PullRequest
    {
        get;
    }
    /// <summary>One-based layer number for display.</summary>
    public int Number => Snapshot.Position + 1;
    public bool IsTip => Number == Group?.Definition.Branches.Count;
    public string Name => StackRefs.Short(Snapshot.Branch);
    public string? LocalBranchName => Snapshot.Branch.StartsWith(LocalBranch.Prefix, StringComparison.Ordinal) ? Name : null;
    public string Title => PullRequest?.Title ?? Name;
    public string CheckLabel => $"Include layer {Number}: {Title}";
    public string Detail => PullRequest is null ? (HasWarning ? "" : $"↑{Snapshot.Ahead}  ↓{Snapshot.Behind}")
        : $"#{PullRequest.Number} · {PullRequest.HeadRef}{(PullRequest.IsDraft ? " · Draft" : "")}";
    public string? Warning => Snapshot.Warning;
    public bool HasWarning => Warning is not null;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Creates presentation state from a snapshot and optional pull-request metadata.</summary>
    public LayerViewModel(StackLayerSnapshot snapshot, PullRequest? pullRequest = null)
    {
        Snapshot = snapshot;
        PullRequest = pullRequest;
    }
}
