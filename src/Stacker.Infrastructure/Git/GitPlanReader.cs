using System.Globalization;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

internal sealed class GitPlanReader(GitRunner git, IGitRepositoryReader reader)
{
    public async Task<RestackPlan> PlanAsync(RepositorySnapshot repository, StackDefinition stack, bool preserveMerges = false, CancellationToken ct = default)
    {
        if (stack.Branches.Count == 0) return new(repository.Root, stack.Id, stack.Name, "", false, preserveMerges, [], ["This stack has no layers."]);
        var preflight = await PreflightAsync(repository, stack, ct);
        return StackSyncPlanner.Plan(repository, stack, preflight, preserveMerges);
    }

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
