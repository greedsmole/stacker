using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

public sealed partial class WorkingTreeAuthoring(IProcessRunner processes, GitExecutable executable, GitRepositoryReader reader,
    CommandBus commands, IJournal journal) : IWorkingTreeAuthoring
{
    private readonly GitRunner _git = new(processes, executable);

    public Task<Result<WorkingTreeSnapshot>> ReadAsync(string root, CancellationToken ct = default) =>
        Boundary.CaptureAsync(() => ReadSnapshotAsync(root, ct), ProblemCodes.GitFailed);

    private async Task<WorkingTreeSnapshot> ReadSnapshotAsync(string root, CancellationToken ct)
    {
        var head = await _git.ShaAsync(root, "HEAD", ct);
        var state = await _git.StatusAsync(root, ct, allUntracked: true);
        var index = await _git.Run(root, ["--no-optional-locks", "diff", "--cached", "--raw", "--no-abbrev", "--no-renames",
            "--no-ext-diff", "--no-textconv", "-z", "--"], ct);
        if (head != await _git.ShaAsync(root, "HEAD", ct))
            throw new StackerException("The checkout changed while reading the working tree. Refresh and try again.");
        return new(root, head, state, StableIdentity.For(index.StdOut));
    }

    public Task<Result<FileDiff>> ReadPatchAsync(WorkingTreeSnapshot snapshot, WorkingTreeFile file, CancellationToken ct = default) =>
        Boundary.CaptureAsync(async () =>
        {
            RequireSelection(snapshot, file.Side, [file]);
            return await reader.WorkingTreePatchAsync(snapshot.Root, file, ct);
        }, ProblemCodes.GitFailed);

    public Task<Result<Unit>> UpdateIndexAsync(WorkingTreeSnapshot snapshot, WorkingTreeSide side, IReadOnlyList<WorkingTreeFile> files,
        CancellationToken ct = default)
    {
        var selected = files.ToArray();
        var staging = side == WorkingTreeSide.Unstaged;
        return RunAsync(snapshot.Root, staging ? "local.stage" : "local.unstage", string.Join("\n", selected.Select(file => file.Path)),
            staging ? "Staged selected files" : "Unstaged selected files", async token =>
            {
                var current = await ReadSnapshotAsync(snapshot.Root, token);
                if (CheckSnapshot(snapshot, current) is { } stale)
                    return Result<Unit>.Fail(stale);
                RequireSelection(current, side, selected);
                var paths = selected.SelectMany(file => file.Status == 'R' && file.OldPath != file.Path
                    ? new[] { file.OldPath, file.Path } : [file.Path]).Distinct(StringComparer.Ordinal);
                var input = string.Join('\0', paths) + '\0';
                var args = staging
                    ? new[] { "--literal-pathspecs", "add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul" }
                    : ["--literal-pathspecs", "reset", "-q", "--pathspec-from-file=-", "--pathspec-file-nul"];
                await _git.Run(snapshot.Root, args, token, input: input);
                return Result<Unit>.Ok(Unit.Value);
            }, ct);
    }

    public Task<Result<string>> CommitStagedAsync(WorkingTreeSnapshot snapshot, string message, CancellationToken ct = default) =>
        RunAsync(snapshot.Root, "local.commit", snapshot.State.Branch ?? "HEAD", "Committed staged files", async token =>
        {
            var current = await ReadSnapshotAsync(snapshot.Root, token);
            if (CheckSnapshot(snapshot, current) is { } stale)
                return Result<string>.Fail(stale);
            if (string.IsNullOrWhiteSpace(message) || !current.State.Entries.Any(entry => entry.IsStaged))
                return Result<string>.Fail(Reject("Stage the files and enter a commit message."));
            var commit = await new WorkingTreeEffects(_git, journal).CommitAsync(snapshot.Root, message.Trim(), stageAll: false, token);
            return Result<string>.Ok(commit);
        }, ct);

    private static Problem? CheckSnapshot(WorkingTreeSnapshot expected, WorkingTreeSnapshot current)
    {
        if (current.State.NeedsAttention)
            return Reject("Finish the current Git operation or resolve conflicts before editing the index.");
        if (expected.Head != current.Head || expected.State.Branch != current.State.Branch || expected.IndexRevision != current.IndexRevision)
            return Reject("The checkout or index changed. Refresh the working tree and try again.");
        return null;
    }

    private static void RequireSelection(WorkingTreeSnapshot snapshot, WorkingTreeSide side, IReadOnlyList<WorkingTreeFile> files)
    {
        if (files.Count == 0)
            throw new StackerException(Reject("Select at least one file."));
        var entries = snapshot.State.Entries.ToHashSet();
        if (files.Any(file => file.Side != side || !file.IsAvailable || !entries.Contains(file.Entry) ||
            !IsRelativeGitPath(file.Path) || !IsRelativeGitPath(file.OldPath) || (file.Status is 'R' or 'C' && file.OldPath == file.Path)))
            throw new StackerException(Reject("A selected file changed. Refresh the working tree and try again."));
    }

    private static bool IsRelativeGitPath(string path) => path.Length > 0 && !Path.IsPathRooted(path) && !path.Contains('\0')
        && (!OperatingSystem.IsWindows() || !path.Contains('\\'))
        && !path.Split('/').Any(part => part is "" or "." or "..");

    private async Task<Result<T>> RunAsync<T>(string root, string kind, string target, string description,
        Func<CancellationToken, Task<Result<T>>> execute, CancellationToken ct)
    {
        var command = new AuthoringCommand<T>(kind, target, description, async token =>
        {
            try { return await execute(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Result<T>.Fail(Boundary.Problem(ex, ProblemCodes.GitFailed)); }
        });
        var outcome = await commands.RunAsync(RepoRoot.Parse(root).OrThrow(), command, ct: ct);
        if (outcome.Ok && command.Value is { } value)
            return value;
        return Result<T>.Fail(outcome.Problem ?? Problem.Of(ProblemCodes.GitFailed, outcome.Message)
            with { Certainty = outcome.Certainty });
    }

    private static Problem Reject(string message) => Problem.Of(ProblemCodes.StateInvalid, message) with { Certainty = EffectCertainty.Rejected };
}
