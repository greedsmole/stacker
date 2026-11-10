using System.Globalization;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Turns one repository snapshot and one stack definition into a restack plan by gathering the read-only facts the planner needs.</summary>
/// <remarks>Planning writes nothing: it reads status, per-layer divergence and fork points, the worktrees that have a branch checked out,
/// and whether an origin exists, then hands those facts to the pure <see cref="StackSyncPlanner"/>. A plan is a snapshot and can go stale
/// between the preview and execution; the effect re-verifies branch tips before moving anything. The fork point is the fact that matters:
/// when a parent was amended or rewritten, only a proven reflog fork point lets the replay cover the layer's own commits, so a missing fork
/// point blocks the layer with an explanation instead of replaying from a guessed base (see <c>docs/ARCHITECTURE.md</c>).</remarks>
/// <param name="git">Runner used for state probes the read-only port does not expose - fork point, commit counts and origin presence.</param>
/// <param name="reader">Bounded read port used for the parent/head divergence counts.</param>
internal sealed class GitPlanReader(GitRunner git, IGitRepositoryReader reader)
{
    /// <summary>Returns the plan for a stack, or a plan whose single blocker explains why there is nothing to plan.</summary>
    /// <param name="repository">Snapshot whose refs are the only source of branch and parent tips.</param>
    /// <param name="stack">Definition whose layer order defines each parent relationship.</param>
    /// <param name="preserveMerges">Whether merge commits are replayed instead of blocking the layer that contains them.</param>
    /// <param name="ct">Cancels the read-only fact gathering; nothing has been written at this point.</param>
    /// <returns>The plan, including blocked layers, the captured remote tips and a user-facing reason per blocked layer.</returns>
    /// <remarks>An empty stack is not an error: the returned plan carries "This stack has no layers." as its only blocker, so the preview
    /// can explain the situation rather than the caller having to treat it as a failure. Otherwise the preflight facts are gathered and
    /// <see cref="StackSyncPlanner.Plan"/> decides every step; the caller must still check <see cref="RestackPlan.CanExecute"/> before
    /// showing an executable action.</remarks>
    public async Task<RestackPlan> PlanAsync(RepositorySnapshot repository, StackDefinition stack, bool preserveMerges = false, CancellationToken ct = default)
    {
        if (stack.Branches.Count == 0) return new(repository.Root, stack.Id, stack.Name, "", false, preserveMerges, [], ["This stack has no layers."]);
        var preflight = await PreflightAsync(repository, stack, ct);
        return StackSyncPlanner.Plan(repository, stack, preflight, preserveMerges);
    }

    /// <summary>Reads the repository and per-layer facts that planning needs, without changing anything.</summary>
    /// <param name="repository">Snapshot that supplies the refs for every layer and its parent.</param>
    /// <param name="stack">Definition whose layer order defines each parent: the base for the first layer, the previous layer afterwards.</param>
    /// <param name="ct">Cancels between layers, so a large stack can be abandoned while it is still being read.</param>
    /// <returns>The status, off-limits worktree branches, origin presence and one state per declared layer.</returns>
    /// <remarks>Flow: <see cref="GitRunner.StatusAsync"/> supplies branch, unfinished operation and dirtiness, and the snapshot's refs become a
    /// name-to-commit dictionary. A layer whose own ref or whose parent ref is missing becomes a zeroed state; the planner blocks it from the
    /// missing ref itself, not from a measured count, so no probe is attempted for a pair that cannot be compared. Otherwise the behind count
    /// comes from <see cref="IGitRepositoryReader.CountsAsync"/>; when it is nonzero the parent has moved, so the replay cutoff is the proven
    /// fork point from <c>merge-base --fork-point</c>. If Git cannot produce one, the cutoff stays null: the layer's own commit count is reported
    /// as zero and it carries a blocker asking for a manual restack, because replaying from the parent tip would replay the wrong range. The own
    /// commit count and the merge flag are measured only inside that cutoff range. Origin presence is one <c>remote get-url origin</c> probe and
    /// decides whether a push can follow the restack; the probe is not an error when the remote is absent.</remarks>
    private async Task<SyncPreflight> PreflightAsync(RepositorySnapshot repository, StackDefinition stack, CancellationToken ct)
    {
        var root = repository.Root;
        var status = await git.StatusAsync(root, ct);
        var refs = repository.Refs.ToDictionary(r => r.Name, r => r.Sha, StringComparer.Ordinal);
        var layers = new List<SyncLayerState>();
        for (var i = 0; i < stack.Branches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var branch = stack.Branches[i];
            var parent = i == 0 ? stack.Base : stack.Branches[i - 1];
            if (!refs.TryGetValue(branch, out var headSha) || !refs.TryGetValue(parent, out var parentSha)) { layers.Add(new(branch, 0, 0, false)); continue; }
            var (_, behind) = (await reader.CountsAsync(root, parentSha, headSha, ct)).OrThrow();
            string? cutoff = parentSha;
            if (behind > 0)
            {
                var fork = await git.Run(root, ["merge-base", "--fork-point", parent, headSha], ct, allowFailure: true);
                cutoff = fork.ExitCode == 0 ? fork.StdOut.Trim() : null;
            }
            var commits = cutoff is null ? 0 : git.ParseCount((await git.Run(root, ["rev-list", "--count", $"{cutoff}..{headSha}"], ct)).StdOut);
            var merges = cutoff is not null && git.ParseCount((await git.Run(root, ["rev-list", "--merges", "--count", $"{cutoff}..{headSha}"], ct)).StdOut) > 0;
            layers.Add(new(branch, behind, commits, merges, cutoff, cutoff is null
                ? $"Cannot establish the original parent of {StackRefs.Short(branch)}. Its reflog is unavailable; restack it manually before retrying." : null));
        }
        var origin = await git.Run(root, ["remote", "get-url", "origin"], ct, allowFailure: true);
        return new(root, status.Branch, status.HasTrackedChanges, status.HasOperation ? WorkingOperations.Noun(status.Operation) : null,
            await git.OtherWorktreeBranchesAsync(root, ct), layers, origin.ExitCode == 0);
    }
}
