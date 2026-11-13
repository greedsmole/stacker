using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One change under review — a local branch or a pull request — whose identity outlives the ref it currently uses.</summary>
/// <remarks>Versions, edges, checkpoints and concerns reference <see cref="Id"/>, so renaming a branch or moving a ref never
/// forks the history. <see cref="Reference"/> is only how the next observation finds a head SHA, and <see cref="State"/> is
/// the last observed state rather than a claim about the remote at this moment. Remote nodes carry a pull request number
/// and are looked up by it; local nodes are looked up by their full ref.</remarks>
/// <param name="Id">Durable identity referenced by version pins and edges; created once and preserved across renames.</param>
/// <param name="Scope">Repository scope owning the node: <c>local:&lt;repository-id&gt;</c> or
/// <c>&lt;host&gt;/&lt;repository-id&gt;/&lt;user&gt;</c>, so two accounts or two clones never share a node by accident.</param>
/// <param name="Title">Display name refreshed from the branch name or pull request title on every observation.</param>
/// <param name="Reference">Moveable full ref used to resolve the layer's current head; never part of the identity.</param>
/// <param name="PullRequest">Pull request number for a remote change, <see langword="null"/> for a local branch.</param>
/// <param name="State">Last observed state: <c>Open</c> or <c>Missing</c> locally; <c>Open</c>, <c>Merged</c> or
/// <c>Closed</c> from GitHub; <c>Unknown</c> when a historical lookup failed. A remote node that leaves the open snapshot is
/// retained with its final or re-read state instead of disappearing.</param>
public sealed record ChangeNode(string Id, string Scope, string Title, string Reference, long? PullRequest = null,
    string State = "Open")
{
    /// <summary>Renders the title, so list and combo bindings show the change rather than the record's fields.</summary>
    public override string ToString() => Title;
}
