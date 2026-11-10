using System.Globalization;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Performs the local working-tree actions the user initiates: reading state, creating a layer, staging,
/// committing, stashing and restoring a stash, and switching branches.</summary>
/// <remarks>Every mutating method runs its guards against one status snapshot and then acts, so a decision cannot be
/// made from a tree state that has already changed underneath it. Two guards recur and mean the same thing each time:
/// tracked changes block an action that would move a branch or create one, because checking out would carry those
/// edits onto another commit, and an unfinished Git operation blocks it because the index and working tree belong to
/// that operation until it is finished or aborted. Untracked files never block anything, because moving a ref cannot
/// lose them. Mutating methods hold <see cref="GitRunner.Gate"/> so they cannot interleave with another writer, while
/// <see cref="StateAsync"/> is read-only and deliberately does not queue behind the gate.</remarks>
/// <param name="git">Runner that owns the process policy, the write gate and the status read.</param>
/// <param name="journal">Journal that owns recorded stashes, so undo and stash restore stay together.</param>
internal sealed class WorkingTreeEffects(GitRunner git, IJournal journal)
{
    /// <summary>Reads the branch, the unfinished operation and every status entry in one snapshot.</summary>
    /// <remarks>Read-only, so it takes no gate: a caller that acts on the result re-checks it inside the mutating
    /// method's guard. The snapshot is intentionally single, so the branch name, the operation and the entries cannot
    /// describe different moments.</remarks>
    /// <param name="root">Repository root to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The current working-tree state, including a null branch when HEAD is detached.</returns>
    public Task<WorkingTreeState> StateAsync(string root, CancellationToken ct = default) => git.StatusAsync(root, ct);

    /// <summary>The subject of a branch's last commit, for labelling the branch in lists.</summary>
    /// <remarks>A missing branch, an unborn branch and a failed read all report <see langword="null"/> instead of an
    /// error, because a label is optional decoration and a stale branch name in the interface must not turn into a
    /// failed refresh. Failure is allowed on the command for that reason; only success with a non-empty subject is
    /// reported.</remarks>
    /// <param name="root">Repository root to read.</param>
    /// <param name="branch">Branch name or full ref to inspect.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The first line of the last commit's message, or null when it cannot be read.</returns>
    public async Task<string?> LastCommitSubjectAsync(string root, string branch, CancellationToken ct = default)
    {
        var result = await git.Run(root, ["log", "-1", "--format=%s", branch], ct, allowFailure: true);
        var subject = result.StdOut.Trim();
        return result.ExitCode == 0 && subject.Length > 0 ? subject : null;
    }

    /// <summary>Creates a new local branch for a layer and checks it out, leaving the working tree clean.</summary>
    /// <remarks>Guards run before anything is created: a blank name is refused, tracked changes or an unfinished
    /// operation are refused because a new branch must start from a clean tree, Git's own <c>check-ref-format</c>
    /// decides whether the name is addressable as a branch, an existing branch is refused so a stack layer is never
    /// silently reused, and a missing start commit is refused because the branch would be created from nothing. Only
    /// then is the branch created from the given ref, so the new layer begins as an exact copy of its parent. The
    /// returned ref is the full <c>refs/heads/...</c> form, which is what the caller records as the layer.</remarks>
    /// <param name="root">Repository root to create the branch in.</param>
    /// <param name="branchName">Name entered by the user; surrounding whitespace is ignored.</param>
    /// <param name="startRef">Ref the layer starts from, normally the top of the stack below it.</param>
    /// <param name="ct">Cancels the checks and the checkout.</param>
    /// <returns>The full ref of the created branch.</returns>
    /// <exception cref="StackerException">The name is blank or invalid, the tree has changes, an operation is in
    /// progress, the branch already exists, or the start ref no longer exists.</exception>
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

    /// <summary>Stages exactly the listed paths for the next commit.</summary>
    /// <remarks>The paths follow a <c>--</c> separator, so a path beginning with a dash is staged as a path instead of
    /// being read as an option. An empty list is refused rather than issued as a no-op. The gate is held because this
    /// writes the index, which must not interleave with another writer.</remarks>
    /// <param name="root">Repository root to stage in.</param>
    /// <param name="paths">Paths relative to the repository root; already deselected paths are not expected.</param>
    /// <param name="ct">Cancels the stage.</param>
    /// <returns>The same path list, for the caller to confirm what it staged.</returns>
    /// <exception cref="StackerException">The list is empty.</exception>
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

    /// <summary>Commits the staged changes, optionally staging everything first, and returns the new commit id.</summary>
    /// <remarks>The guards make a refused commit explain itself rather than repeat Git's message: a blank message is
    /// refused, conflicts must be resolved and staged first, and without stage-all at least one entry must actually be
    /// staged, which is the common "tick stage every change" mistake. The commit runs through the user-facing path so
    /// the user's own commit hooks and signing configuration apply, unlike the commands the application runs for
    /// itself, which disable hooks. The returned id is read back from HEAD because Git's own output is not parsed for
    /// it; a commit that cannot be read back is reported as a failure instead of returning an empty id.</remarks>
    /// <param name="root">Repository root to commit in.</param>
    /// <param name="message">Commit message; whitespace-only text is refused.</param>
    /// <param name="stageAll">Whether to stage every change, including deletions and untracked files, before
    /// committing.</param>
    /// <param name="ct">Cancels the checks, the stage and the commit.</param>
    /// <returns>The full commit id of the new HEAD.</returns>
    /// <exception cref="StackerException">The message is blank, conflicts remain, nothing is staged, Git refused the
    /// commit, or the new commit cannot be read back.</exception>
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

    /// <summary>Saves tracked changes as a stash and attaches its commit to a journal entry.</summary>
    /// <remarks>A clean tree is a normal outcome and returns <see langword="null"/>; untracked files are deliberately
    /// not stashed, because they cannot block a rewrite and stashing them would surprise a user who only wanted a
    /// clean tracked tree. The new stash is attached to the newest undoable entry, otherwise to the active entry,
    /// rather than opening a competing entry, so undo and stash restore remain one record; when neither exists a
    /// standalone <c>stash</c> entry is opened and closed as needing the user so the stash appears in the saved list.
    /// The commit id is read from <c>refs/stash</c> after the push, which is the stash just created.</remarks>
    /// <param name="root">Repository root to stash in.</param>
    /// <param name="message">Message shown in Git's stash list and in the journal.</param>
    /// <param name="ct">Cancels the stash and the journal writes.</param>
    /// <returns>The stash commit id, or null when the tree had no tracked changes to save.</returns>
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

    /// <summary>Restores the stash recorded on an entry and clears that record.</summary>
    /// <remarks>The stash is located by its commit id in Git's current stash list rather than by a remembered
    /// position, because pushing, popping or dropping another stash shifts every position; an id that is no longer
    /// listed is reported as gone instead of popping the wrong stash. The tree must have no operation in progress and
    /// no tracked changes, so the pop applies against the clean tree the stash was taken from. A pop that reports a
    /// conflict is not treated as a failure: Git keeps the stash in that case, the applied changes are left for the
    /// user to resolve, and the journal record is cleared so the same stash is not offered again. Any other nonzero
    /// exit is reported with Git's error. The returned state is read after the pop so the caller needs no second
    /// read.</remarks>
    /// <param name="root">Repository root to restore into.</param>
    /// <param name="ct">Cancels the checks, the pop and the journal write.</param>
    /// <param name="target">Entry holding the stash, or null to use the active or newest undoable entry.</param>
    /// <returns>The working-tree state after the pop.</returns>
    /// <exception cref="StackerException">No entry holds a stash, the tree has changes or an operation in progress, the
    /// stash is no longer listed, or the pop failed for a reason other than a conflict.</exception>
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

    /// <summary>Checks out an existing local branch when the tree is clean enough to switch.</summary>
    /// <remarks>The guards keep a switch from carrying work across branches or colliding with an operation: tracked
    /// changes are refused, an unfinished operation is refused, and a branch that does not exist is refused before any
    /// checkout. Switching to the branch already checked out returns without running Git, which avoids rewriting files
    /// and refreshing index metadata for no change. A failing checkout is reported with Git's own reason.</remarks>
    /// <param name="root">Repository root to switch in.</param>
    /// <param name="branch">Branch name or full ref to check out.</param>
    /// <param name="ct">Cancels the checks and the checkout.</param>
    /// <exception cref="StackerException">The tree has changes, an operation is in progress, the branch does not exist,
    /// or the checkout failed.</exception>
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
