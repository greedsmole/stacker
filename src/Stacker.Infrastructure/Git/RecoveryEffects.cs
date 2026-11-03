using System.Globalization;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

internal sealed class RecoveryEffects(GitRunner git, IJournal journal)
{
    public async Task<RestackOutcome> AbortAsync(string root, CancellationToken ct = default, JournalEntry? target = null)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var entry = target ?? (await journal.ActiveAsync(root, ct)).OrThrow();
            var state = await git.StatusAsync(root, ct);
            // An old applied restack or a failed push does not own a terminal-started operation.
            if (entry is not { Kind: "restack", NeedsAttention: true }) entry = null;
            if (!state.HasOperation && entry is null)
                return git.Refused("There is no interrupted restack to restore. Inspect any uncertain effects before dismissing their record.");
            var restored = await RestoreAsync(root, entry, entry is null ? "" : git.OriginalBranch(entry), ct, close: false);
            var message = restored == 0 ? "The operation was aborted." : $"The operation was aborted and {restored} branch(es) were restored.";
            if (entry is not null) entry = (await journal.CloseAsync(entry, JournalPhase.RolledBack, message: message, ct: ct)).OrThrow();
            return new(RestackStatus.RolledBack, null, [], message, entry);
        }
        finally { git.Gate.Release(); }
    }

    public async Task<int> RestoreAsync(string root, JournalEntry? entry, string originalBranch, CancellationToken ct, bool close = true)
    {
        var operation = await git.OperationAsync(root, ct);
        if (operation != WorkingOperation.None) await git.Run(root, git.AbortArgs(operation), ct);
        if ((await git.StatusAsync(root, ct)).HasTrackedChanges)
            throw new StackerException("Recovery stopped: the working tree contains new changes. Commit or stash them first.");
        var moves = new List<JournalStep>();
        var elsewhere = await git.OtherWorktreeBranchesAsync(root, ct);
        foreach (var step in entry?.Restorable ?? [])
        {
            var sha = await git.ShaAsync(root, step.Target, ct);
            if (sha == step.OldSha) continue;
            if (sha is null || step.NewSha is null || sha != step.NewSha || elsewhere.Contains(step.Target))
                throw new StackerException($"Recovery cannot prove ownership of {StackRefs.Short(step.Target)}. Its current commit was preserved; inspect Git before continuing.");
            moves.Add(step);
        }
        await git.RestoreRefsAsync(root, moves, originalBranch, ct);
        if (close && entry is not null)
            (await journal.CloseAsync(entry, JournalPhase.RolledBack, message: $"Every branch was restored ({moves.Count}).", ct: ct)).OrThrow();
        return moves.Count;
    }

    public Task<JournalEntry?> LastJournalAsync(string root, CancellationToken ct = default) => Boundary.UnwrapAsync(journal.UndoableAsync(root, ct));

    public async Task<JournalEntry?> ActiveJournalAsync(string root, CancellationToken ct = default) =>
        (await journal.ActiveAsync(root, ct)).OrThrow() ?? (await journal.UndoableAsync(root, ct)).OrThrow();

    public async Task DismissAsync(string root, CancellationToken ct = default, JournalEntry? target = null)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var entry = target ?? await ActiveJournalAsync(root, ct) ?? throw new StackerException("There is nothing to dismiss in this repository.");
            (await journal.CloseAsync(entry, JournalPhase.Dismissed, message: entry.Message, ct: ct)).OrThrow();
        }
        finally { git.Gate.Release(); }
    }

    public async Task<RestackOutcome> UndoLastAsync(string root, CancellationToken ct = default)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var entry = (await journal.UndoableAsync(root, ct)).OrThrow() ?? throw new StackerException("There is no restack to undo in this repository.");
            var state = await git.StatusAsync(root, ct);
            if (state.HasTrackedChanges) throw new StackerException("The working tree has uncommitted changes. Commit or stash them before undoing.");
            if (state.HasOperation) throw new StackerException($"{WorkingOperations.Noun(state.Operation)} is in progress. Finish or abort it before undoing.");
            var elsewhere = await git.OtherWorktreeBranchesAsync(root, ct);
            var moves = entry.Moved.Where(step => step.Kind == "rebase" && step.NewSha is not null).ToArray();
            foreach (var step in moves)
            {
                if (elsewhere.Contains(step.Target)) throw new StackerException($"{StackRefs.Short(step.Target)} is checked out in another worktree.");
                var sha = await git.ShaAsync(root, step.Target, ct);
                if (sha != step.NewSha)
                    throw new StackerException($"{StackRefs.Short(step.Target)} moved after the restack. Undo would discard that work, so nothing was changed.");
            }
            await git.RestoreRefsAsync(root, moves, git.OriginalBranch(entry), ct);
            entry = (await journal.CloseAsync(entry, JournalPhase.Undone,
                message: $"Restored {entry.Moved.Count} branch(es) to their commits before the restack. Remote branches were not changed.", ct: ct)).OrThrow();
            return new(RestackStatus.RolledBack, null, [],
                $"Restored {entry.Moved.Count} branch(es) to their commits before the restack. Remote branches were not changed.", entry);
        }
        finally { git.Gate.Release(); }
    }
}
