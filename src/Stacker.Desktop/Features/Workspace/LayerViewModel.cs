using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
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
    public string Name => Snapshot.Branch.Replace("refs/heads/", "").Replace("refs/remotes/", "");
    public string Title => PullRequest?.Title ?? Name;
    public string Detail => PullRequest is null ? $"↑{Snapshot.Ahead}  ↓{Snapshot.Behind}" : $"#{PullRequest.Number} · {PullRequest.HeadRef}{(PullRequest.IsDraft ? " · Draft" : "")}";
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
