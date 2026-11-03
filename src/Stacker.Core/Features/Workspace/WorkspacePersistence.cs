using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Writes only changed stack views and a small index; imports the monolithic layout on first save.</summary>
public sealed class WorkspacePersistence(IApplicationState store)
{
    private readonly Dictionary<(string Root, string Stack), long> _saved = [];
    private readonly Dictionary<string, StackViewState> _snapshots = [];
    private string? _snapshotRoot;
    public WorkspaceState Snapshot(string root, WorkspaceState state)
    {
        if (_snapshotRoot != root) { _snapshots.Clear(); _snapshotRoot = root; }
        var copy = new WorkspaceState { ActiveGroup = state.ActiveGroup };
        foreach (var (key, stack) in state.Stacks)
        {
            if (!_snapshots.TryGetValue(key, out var cached) || cached.Revision != stack.Revision)
                _snapshots[key] = cached = stack.Snapshot();
            copy.Stacks[key] = cached;
        }
        return copy;
    }
    public Problem? LastProblem { get; private set; }
    public async Task<Result<WorkspaceState>> LoadAsync(string root, CancellationToken ct = default)
    {
        _snapshots.Clear();
        foreach (var key in _saved.Keys.Where(key => key.Root == root).ToArray()) _saved.Remove(key);
        var result = await store.ReadAsync<WorkspaceState>("workspace", root, ct);
        if (result.IsFailed) return Result<WorkspaceState>.Fail(result.Problem!);
        var state = result.Value ?? new();
        if (state.SchemaVersion > 2) return Result<WorkspaceState>.Fail(ProblemCodes.StateInvalid, "This workspace was saved by a newer Stacker version.");
        state.Stacks ??= []; state.StackKeys ??= [];
        if (state.SchemaVersion == 2)
            foreach (var key in state.StackKeys.Distinct())
            {
                var stack = await store.ReadAsync<StackViewState>("workspace-stacks", root + "|" + key, ct);
                if (stack.IsFailed) return Result<WorkspaceState>.Fail(stack.Problem!);
                state.Stacks[key] = stack.Value ?? new();
                _saved[(root, key)] = 0;
            }
        foreach (var key in state.Stacks.Keys.ToArray())
        {
            var stack = state.Stacks[key] ??= new();
            stack.Branches ??= []; stack.Comparisons ??= [];
            stack.Comparisons = stack.Comparisons.Where(pair => pair.Value is not null).TakeLast(40).ToDictionary();
            if (!Enum.IsDefined(stack.Mode)) stack.Mode = DiffMode.FullStack;
        }
        return Result<WorkspaceState>.Ok(state);
    }

    public async Task<Result<Unit>> SaveAsync(string root, WorkspaceState snapshot, CancellationToken ct = default)
    {
        foreach (var (key, stack) in snapshot.Stacks)
        {
            if (_saved.TryGetValue((root, key), out var version) && version == stack.Revision) continue;
            var written = await store.WriteAsync("workspace-stacks", root + "|" + key, stack, ct);
            if (written.IsFailed) { LastProblem = written.Problem; return written; }
            _saved[(root, key)] = stack.Revision;
        }
        var index = new WorkspaceState { SchemaVersion = 2, ActiveGroup = snapshot.ActiveGroup, StackKeys = snapshot.Stacks.Keys.ToArray() };
        var result = await store.WriteAsync("workspace", root, index, ct);
        LastProblem = result.Problem;
        return result;
    }
}
