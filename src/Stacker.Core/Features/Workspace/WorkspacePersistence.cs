using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Writes only changed stack views and a small index; imports the monolithic layout on first save.</summary>
/// <param name="store">Application state store that owns the workspace documents; every read and write goes through
/// it, so this type never touches the profile layout directly.</param>
/// <remarks>Two layouts share this type. Layout 1 is the historical single document: one <c>workspace</c> document
/// carries every stack view inline. Layout 2 keeps a small index under the same identity and one
/// <c>workspace-stacks</c> document per stack, keyed <c>root|stack</c>. A loaded layout-1 document is converted on the
/// next save: its stack views are written as individual documents first and the index replaces the single document
/// last, so a conversion that fails part way leaves the old document, and the only copy of that state, readable.
/// <para>The write order makes the index the commit point. Stack documents are written first and the index last,
/// because the index is the only document that says which stacks exist: writing it first would advertise a stack
/// whose document had not landed, and a load would then read a missing view as empty state. A failed stack write
/// returns before the index is touched, leaving the previous index valid and the failed key unmarked for a retry.</para>
/// <para><see cref="Snapshot"/> clones state before it is queued, so a save that waits behind another write cannot
/// observe UI mutations made in the meantime. Revisions are the comparison key for both cloning and skipping writes,
/// which is why they are deliberately not persisted.</para></remarks>
public sealed class WorkspacePersistence(IApplicationState store)
{
    /// <summary>Revision already on disk per (root, stack), so a stack document whose view did not change is not
    /// rewritten.</summary>
    /// <remarks>Recorded with the revision that was written, and seeded with 0 for a stack document read from this
    /// same root, because a loaded view always starts at revision 0. A stack imported from a legacy root is not
    /// marked, so it is written under the current root on the first save. <see cref="LoadAsync"/> drops the entries for
    /// its root so a reload re-checks the disk instead of trusting a marker from an earlier session.</remarks>
    private readonly Dictionary<(string Root, string Stack), long> _saved = [];
    /// <summary>Last cloned view per stack key, reused while the live view is unchanged.</summary>
    /// <remarks>The cache belongs to one root only: two roots can hold different views under the same stack key, which
    /// is why <see cref="_snapshotRoot"/> guards reuse and why a load drops the cache outright.</remarks>
    private readonly Dictionary<string, StackViewState> _snapshots = [];
    /// <summary>The root the cached clones belong to; a different root discards the cache before it is used.</summary>
    private string? _snapshotRoot;
    /// <summary>Takes the copy to queue for saving: the active group plus one cloned view per stack.</summary>
    /// <param name="root">Workspace key the copy will be saved under.</param>
    /// <param name="state">The live workspace state, which the UI keeps mutating.</param>
    /// <returns>A detached copy. It carries no stack keys and keeps the default schema version, because
    /// <see cref="SaveAsync"/> builds the index (schema 2 plus the stack keys) from the copy when it saves.</returns>
    /// <remarks>A stack whose <see cref="StackViewState.Revision"/> still matches the cached clone reuses that clone
    /// instead of copying again, so an idle capture allocates nothing per stack. Everything else is deep-copied through
    /// <see cref="StackViewState.Snapshot"/>, which is what keeps a queued save stable: a later UI change bumps the live
    /// revision but cannot reach the copy being written. A reused clone is shared with earlier copies, so callers must
    /// treat the returned views as read-only. The root change check runs first, because cached clones from another root
    /// are not comparable even when the stack key matches.</remarks>
    public WorkspaceState Snapshot(string root, WorkspaceState state)
    {
        if (_snapshotRoot != root)
        {
            _snapshots.Clear();
            _snapshotRoot = root;
        }
        var copy = new WorkspaceState { ActiveGroup = state.ActiveGroup };
        foreach (var (key, stack) in state.Stacks)
        {
            if (!_snapshots.TryGetValue(key, out var cached) || cached.Revision != stack.Revision)
                _snapshots[key] = cached = stack.Snapshot();
            copy.Stacks[key] = cached;
        }
        return copy;
    }
    /// <summary>The failure from the most recent save, or <see langword="null"/> when the last index write
    /// succeeded.</summary>
    /// <remarks>Set by a failing stack or index write and cleared by a successful index write, so it describes the last
    /// attempt rather than a historical failure. Callers gate profile export and the close decision on it, so a
    /// workspace that could not be saved is reported instead of being silently exported or closed.</remarks>
    public Problem? LastProblem
    {
        get; private set;
    }
    /// <summary>Loads one workspace, importing the older document when the current key has no state yet.</summary>
    /// <param name="root">Current workspace key: the worktree identity where one is known, otherwise the repository
    /// path.</param>
    /// <param name="ct">Cancels between document reads.</param>
    /// <param name="legacyRoot">Path-based key used before worktree identities existed. Read only when
    /// <paramref name="root"/> holds no document and the two keys differ.</param>
    /// <returns>The normalized state, or the failure from any document that could not be read.</returns>
    /// <remarks>Nothing is written here: a load only reports what the documents contain, and the next save performs any
    /// conversion. The clone cache is dropped and the saved-revision markers for this root are forgotten first, so the
    /// result describes the documents on disk now rather than the previous session's assumptions. When the current key
    /// has no document, the same read is repeated once at the legacy key and the local <c>source</c> keeps pointing
    /// there for the per-stack reads, so a migration cannot mix stacks from two roots. A schema newer than layout 2 is
    /// refused instead of being rewritten, because a downgrade would silently drop fields it does not understand.
    /// <para>A layout-2 index names its stacks through <see cref="WorkspaceState.StackKeys"/>; a stack document that is
    /// missing becomes an empty view rather than a failure, and a missing index is a first open, reported as a new empty
    /// workspace. A damaged document is not replaced here: the failure is passed through so the caller can decide to
    /// continue from an empty workspace and show the notice once. A stack read from the current root is marked as
    /// already saved, while one imported from the legacy root is not, so the next save writes the imported views under
    /// the new key. The final normalization tolerates explicit nulls in the stored documents and keeps only the last 40
    /// comparison views per stack, which is the same bound the UI applies while capturing. An undefined
    /// <see cref="DiffMode"/> is replaced by <see cref="DiffMode.FullStack"/> because JSON can carry an integer that
    /// no mode maps to, and a mode switch would otherwise have no branch to take.</para></remarks>
    public async Task<Result<WorkspaceState>> LoadAsync(string root, CancellationToken ct = default, string? legacyRoot = null)
    {
        _snapshots.Clear();
        foreach (var key in _saved.Keys.Where(key => key.Root == root).ToArray())
            _saved.Remove(key);
        var result = await store.ReadAsync<WorkspaceState>("workspace", root, ct);
        var source = root;
        if (result.IsOk && result.Value is null && legacyRoot is not null && legacyRoot != root)
        {
            source = legacyRoot;
            result = await store.ReadAsync<WorkspaceState>("workspace", source, ct);
        }
        if (result.IsFailed)
            return Result<WorkspaceState>.Fail(result.Problem!);
        var state = result.Value ?? new();
        if (state.SchemaVersion > 2)
            return Result<WorkspaceState>.Fail(ProblemCodes.StateInvalid, "This workspace was saved by a newer Stacker version.");
        state.Stacks ??= [];
        state.StackKeys ??= [];
        if (state.SchemaVersion == 2)
            foreach (var key in state.StackKeys.Distinct())
            {
                var stack = await store.ReadAsync<StackViewState>("workspace-stacks", source + "|" + key, ct);
                if (stack.IsFailed)
                    return Result<WorkspaceState>.Fail(stack.Problem!);
                state.Stacks[key] = stack.Value ?? new();
                if (source == root)
                    _saved[(root, key)] = 0;
            }
        foreach (var key in state.Stacks.Keys.ToArray())
        {
            var stack = state.Stacks[key] ??= new();
            stack.Branches ??= [];
            stack.Comparisons ??= [];
            stack.Comparisons = stack.Comparisons.Where(pair => pair.Value is not null).TakeLast(40).ToDictionary();
            if (!Enum.IsDefined(stack.Mode))
                stack.Mode = DiffMode.FullStack;
        }
        return Result<WorkspaceState>.Ok(state);
    }

    /// <summary>Persists the changed stack views and then the index that names them.</summary>
    /// <param name="root">Workspace key the documents belong to.</param>
    /// <param name="snapshot">A copy produced by <see cref="Snapshot"/>; the live state is never read here.</param>
    /// <param name="ct">Cancels between document writes.</param>
    /// <returns><c>Ok</c> only after the index write succeeded.</returns>
    /// <remarks>Stack documents are compared against their recorded revision and skipped when unchanged, so a save that
    /// only moved the active group costs one small write. The index is written unconditionally and last: it is the
    /// document that declares the stack set, so it must not be newer than the stack documents it names. A failed stack
    /// write returns immediately without touching the index, leaving the previous index valid and the failed key
    /// unmarked for the retry, while a stack that already landed stays marked so the retry rewrites only the index. The
    /// index is always written as schema 2, which is what upgrades a layout-1 workspace on its first save; a stack
    /// removed from the snapshot simply stops being referenced by the new index, and its document is left on disk.
    /// <para>The revision comparison avoids redundant writes; it is not a compare-and-swap, and a whole stack document is
    /// replaced rather than merged. Two processes saving the same profile would therefore keep the later document, which
    /// is why startup holds a single-instance lock instead of relying on this marker for exclusion.</para></remarks>
    public async Task<Result<Unit>> SaveAsync(string root, WorkspaceState snapshot, CancellationToken ct = default)
    {
        foreach (var (key, stack) in snapshot.Stacks)
        {
            if (_saved.TryGetValue((root, key), out var version) && version == stack.Revision)
                continue;
            var written = await store.WriteAsync("workspace-stacks", root + "|" + key, stack, ct);
            if (written.IsFailed)
            {
                LastProblem = written.Problem;
                return written;
            }
            _saved[(root, key)] = stack.Revision;
        }
        var index = new WorkspaceState { SchemaVersion = 2, ActiveGroup = snapshot.ActiveGroup, StackKeys = snapshot.Stacks.Keys.ToArray() };
        var result = await store.WriteAsync("workspace", root, index, ct);
        LastProblem = result.Problem;
        return result;
    }
}
