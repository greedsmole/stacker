using System.Globalization;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

internal sealed class WorkingTreeEffects(GitRunner git, IJournal journal)
{
    public Task<WorkingTreeState> StateAsync(string root, CancellationToken ct = default) => git.StatusAsync(root, ct);

    public async Task<string?> LastCommitSubjectAsync(string root, string branch, CancellationToken ct = default)
    {
        var result = await git.Run(root, ["log", "-1", "--format=%s", branch], ct, allowFailure: true);
        var subject = result.StdOut.Trim();
        return result.ExitCode == 0 && subject.Length > 0 ? subject : null;
    }

    public async Task<string> CreateLayerAsync(string root, string branchName, string startRef, CancellationToken ct = default)
    {
        var name = StackRefs.Short(branchName.Trim());
        if (name.Length == 0) throw new StackerException("Enter a branch name for the new layer.");
        var state = await git.StatusAsync(root, ct);
        if (state.HasTrackedChanges) throw new StackerException("The working tree has uncommitted changes. Commit or stash them before creating a layer.");
        if (state.HasOperation) throw new StackerException($"{WorkingOperations.Noun(state.Operation)} is in progress. Finish or abort it before creating a layer.");
        var format = await git.Run(root, ["check-ref-format", "--branch", name], ct, allowFailure: true);
        if (format.ExitCode != 0) throw new StackerException($"'{name}' is not a valid branch name.");
        if (await git.ShaAsync(root, "refs/heads/" + name, ct) is not null)
            throw new StackerException($"{name} already exists. Choose another name or edit the stack instead.");
        if (await git.ShaAsync(root, startRef, ct) is null)
            throw new StackerException($"Cannot start from {StackRefs.Short(startRef)}: that ref no longer exists.");
        await git.Run(root, ["checkout", "-b", name, startRef], ct);
        return "refs/heads/" + name;
    }

    public async Task<IReadOnlyList<string>> StageAsync(string root, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) throw new StackerException("There is nothing to stage.");
        await git.Gate.WaitAsync(ct);
        try
        {
            await git.Run(root, ["add", "--", .. paths], ct);
            return paths;
        }
        finally { git.Gate.Release(); }
    }

    public async Task<string> CommitAsync(string root, string message, bool stageAll, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new StackerException("Enter a commit message.");
        await git.Gate.WaitAsync(ct);
        try
        {
            var state = await git.StatusAsync(root, ct);
            if (state.HasConflicts) throw new StackerException("The conflicted files must be resolved and staged first.");
            if (stageAll) await git.Run(root, ["add", "--all"], ct);
            else if (!state.Entries.Any(entry => entry.IsStaged)) throw new StackerException("Nothing is staged. Tick \"stage every change\" or stage the files in your editor.");
            var commit = await git.RunUser(root, ["commit", "-m", message], ct);
            if (commit.ExitCode != 0) throw new StackerException($"Git refused the commit. {(commit.StdErr.Trim() + " " + commit.StdOut.Trim()).Trim()}");
            return await git.ShaAsync(root, "HEAD", ct) ?? throw new StackerException("The new commit could not be read back.");
        }
        finally { git.Gate.Release(); }
    }

    public async Task<string?> StashAsync(string root, string message, CancellationToken ct = default)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var state = await git.StatusAsync(root, ct);
            if (!state.HasTrackedChanges) return null;
            await git.Run(root, ["stash", "push", "--message", message], ct);
            var stash = await git.ShaAsync(root, "refs/stash", ct);
            if (stash is null) return null;
            // Never a competing entry: attaching to the active one keeps undo and the stash together.
            var holder = (await journal.UndoableAsync(root, ct)).OrThrow() ?? (await journal.ActiveAsync(root, ct)).OrThrow();
            if (holder is not null) (await journal.SetStashAsync(holder, stash, ct)).OrThrow();
            else (await journal.CloseAsync((await journal.OpenAsync(root, "stash", [], ct)).OrThrow(), JournalPhase.NeedsUser, stash, message: "Stashed changes are waiting to be restored.", ct: ct)).OrThrow();
            return stash;
        }
        finally { git.Gate.Release(); }
    }

    public async Task<WorkingTreeState> RestoreStashAsync(string root, CancellationToken ct = default, JournalEntry? target = null)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var holder = target ?? (await journal.ActiveAsync(root, ct)).OrThrow() ?? (await journal.UndoableAsync(root, ct)).OrThrow();
            var stash = holder?.StashRef ?? throw new StackerException("There are no stashed changes to restore.");
            var state = await git.StatusAsync(root, ct);
            if (state.HasOperation || state.HasTrackedChanges)
                throw new StackerException("Finish the current Git operation and commit or stash tracked changes before restoring a saved stash.");
            var list = (await git.Run(root, ["stash", "list", "--format=%H"], ct)).StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var index = Array.IndexOf(list, stash);
            if (index < 0) throw new StackerException("The stashed changes are no longer in the stash list. Inspect them with git stash list before removing anything.");
            var pop = await git.Run(root, ["stash", "pop", $"stash@{{{index}}}"], ct, allowFailure: true);
            var failed = pop.ExitCode != 0 && !(pop.StdErr + pop.StdOut).Contains("CONFLICT", StringComparison.OrdinalIgnoreCase);
            if (failed) throw new StackerException($"Restoring the stashed changes failed. {pop.StdErr.Trim()}");
            (await journal.SetStashAsync(holder!, null, ct)).OrThrow();
            return await git.StatusAsync(root, ct);
        }
        finally { git.Gate.Release(); }
    }

    public async Task SwitchAsync(string root, string branch, CancellationToken ct = default)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var name = StackRefs.Short(branch);
            var state = await git.StatusAsync(root, ct);
            if (state.HasTrackedChanges) throw new StackerException("The working tree has uncommitted changes. Commit or stash them before switching.");
            if (state.HasOperation) throw new StackerException($"{WorkingOperations.Noun(state.Operation)} is in progress. Finish or abort it before switching.");
            if (await git.ShaAsync(root, "refs/heads/" + name, ct) is null) throw new StackerException($"{name} does not exist.");
            if (string.Equals(state.Branch, name, StringComparison.Ordinal)) return;
            var checkout = await git.Run(root, ["checkout", name], ct, allowFailure: true);
            if (checkout.ExitCode != 0) throw new StackerException($"Switching to {name} failed. {checkout.StdErr.Trim()}");
        }
        finally { git.Gate.Release(); }
    }
}
