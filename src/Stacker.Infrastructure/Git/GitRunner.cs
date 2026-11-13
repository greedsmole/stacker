using System.Globalization;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Owns process policy, Git state reads and transactional ref changes.</summary>
internal sealed class GitRunner(IProcessRunner runner, GitExecutable executable)
{
    internal const string BackupRoot = "refs/stacker/backup";
    internal const int RetainedBackups = 10;
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan InteractiveTimeout = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(30);
    internal static readonly IReadOnlyDictionary<string, string> WriteEnvironment = new Dictionary<string, string>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "Never",
        ["GIT_PAGER"] = "cat",
        ["GIT_EDITOR"] = "true",
        ["GIT_SEQUENCE_EDITOR"] = "true",
        ["LC_ALL"] = "C"
    };
    public readonly SemaphoreSlim Gate = new(1, 1);

    public async Task RestoreRefsAsync(string root, IReadOnlyList<JournalStep> moves, string originalBranch, CancellationToken ct)
    {
        var current = await CurrentBranchRefAsync(root, ct);
        var detached = moves.Any(step => step.Target == current);
        if (detached)
            await Run(root, ["checkout", "--detach", "HEAD"], ct);
        var input = "start\n" + string.Concat(moves.Select(step => $"update {step.Target} {step.OldSha} {step.NewSha}\n")) + "prepare\ncommit\n";
        var result = (await runner.RunAsync(new(executable.Resolve(), ["update-ref", "--stdin"], root,
            WriteTimeout, StandardInput: input, Environment: WriteEnvironment), ct)).OrThrow();
        if (result.ExitCode != 0)
        {
            if (detached)
                await SwitchToAsync(root, current, ct, required: true);
            throw new StackerException("The ref transaction was refused; no branches were changed. " + result.StdErr.Trim());
        }
        await SwitchToAsync(root, originalBranch.Length > 0 ? originalBranch : current, ct, required: true);
    }

    public async Task<string?> VerifyPlanAsync(RestackPlan plan, CancellationToken ct)
    {
        var root = plan.RepositoryRoot;
        var state = await StatusAsync(root, ct);
        if (state.HasTrackedChanges)
            return "The working tree changed after the preview was built. Close it, commit or stash, then open the preview again.";
        if (state.HasOperation)
            return $"{WorkingOperations.Noun(state.Operation)} started after the preview was built. Finish or abort it first.";
        foreach (var step in plan.Steps.Where(step => step.NeedsRebase && !step.Blocked))
        {
            if (await ShaAsync(root, step.Branch, ct) != step.BranchTip)
                return $"{step.BranchName} moved after the preview was built. Refresh and restack again.";
            if (await ShaAsync(root, step.ParentRef, ct) != step.OldParentTip)
                return $"{step.ParentName} moved after the preview was built. Refresh and restack again.";
        }
        return null;
    }

    public async Task PruneBackupsAsync(string root, CancellationToken ct)
    {
        var output = (await Run(root, ["for-each-ref", "--format=%(refname)", BackupRoot], ct)).StdOut;
        var stamps = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(reference => reference.Split('/'))
            .Where(parts => parts.Length > 4)
            .GroupBy(parts => parts[3], StringComparer.Ordinal)
            .OrderByDescending(group => group.Key, StringComparer.Ordinal)
            .Skip(RetainedBackups).Select(group => group.Key).ToArray();
        foreach (var stamp in stamps)
        {
            var references = (await Run(root, ["for-each-ref", "--format=%(refname)", BackupRoot + "/" + stamp], ct)).StdOut;
            foreach (var reference in references.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await Run(root, ["update-ref", "-d", reference], ct, allowFailure: true);
        }
    }

    public async Task<WorkingTreeState> StatusAsync(string root, CancellationToken ct)
    {
        var porcelain = (await Run(root, ["status", "--porcelain=v1", "-z", "--untracked-files=normal"], ct, allowFailure: true)).StdOut;
        var head = await Run(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct, allowFailure: true);
        var branch = head.ExitCode == 0 && head.StdOut.Trim().Length > 0 ? head.StdOut.Trim() : null;
        return new(branch, await OperationAsync(root, ct), WorkingTreeParser.Parse(porcelain));
    }

    public async Task<WorkingOperation> OperationAsync(string root, CancellationToken ct)
    {
        foreach (var (name, operation) in new[] { ("rebase-merge", WorkingOperation.Rebase), ("rebase-apply", WorkingOperation.Rebase), ("MERGE_HEAD", WorkingOperation.Merge), ("CHERRY_PICK_HEAD", WorkingOperation.CherryPick), ("REVERT_HEAD", WorkingOperation.Revert) })
        {
            var path = await GitPathAsync(root, name, ct);
            if (path is not null && (File.Exists(path) || Directory.Exists(path)))
            {
                // git am shares rebase-apply with the apply backend; only it leaves an "applying" marker.
                if (name == "rebase-apply" && await GitPathAsync(root, "rebase-apply/applying", ct) is { } applying && File.Exists(applying))
                    return WorkingOperation.Am;
                return operation;
            }
        }
        return WorkingOperation.None;
    }

    public async Task<string?> GitPathAsync(string root, string name, CancellationToken ct)
    {
        var path = (await Run(root, ["rev-parse", "--git-path", name], ct, allowFailure: true)).StdOut.Trim();
        if (path.Length == 0)
            return null;
        return Path.IsPathRooted(path) ? path : Path.Combine(root, path);
    }

    /// <summary>Matches a stopped rebase to the saved branch, original tip and approved destination in this worktree.</summary>
    internal async Task<bool> MatchesRebaseAsync(string root, JournalStep pending, string? expectedOnto, CancellationToken ct)
    {
        foreach (var name in new[] { "rebase-merge", "rebase-apply" })
        {
            var directory = await GitPathAsync(root, name, ct);
            if (directory is null || !Directory.Exists(directory))
                continue;
            var headName = Path.Combine(directory, "head-name");
            var originalHead = Path.Combine(directory, "orig-head");
            var onto = Path.Combine(directory, "onto");
            return File.Exists(headName) && File.Exists(originalHead) &&
                (await File.ReadAllTextAsync(headName, ct)).Trim() == pending.Target &&
                (await File.ReadAllTextAsync(originalHead, ct)).Trim() == pending.OldSha &&
                (expectedOnto is null || File.Exists(onto) && (await File.ReadAllTextAsync(onto, ct)).Trim() == expectedOnto);
        }
        return false;
    }

    public async Task<IReadOnlyList<string>> OtherWorktreeBranchesAsync(string root, CancellationToken ct)
    {
        var output = (await Run(root, ["worktree", "list", "--porcelain"], ct)).StdOut;
        var branches = new List<string>();
        var isCurrent = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                isCurrent = string.Equals(Path.GetFullPath(line[9..]), Path.GetFullPath(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                continue;
            }
            if (!isCurrent && line.StartsWith("branch ", StringComparison.Ordinal))
                branches.Add(line[7..].Trim());
        }
        return branches;
    }

    public async Task<IReadOnlyList<string>> ConflictsAsync(string root, CancellationToken ct)
    {
        var output = (await Run(root, ["diff", "--name-only", "--diff-filter=U", "-z"], ct, allowFailure: true)).StdOut;
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToArray();
    }

    public async Task<string?> ShaAsync(string root, string reference, CancellationToken ct)
    {
        var result = await Run(root, ["rev-parse", "--verify", "--quiet", reference + "^{commit}"], ct, allowFailure: true);
        var value = result.StdOut.Trim();
        return result.ExitCode == 0 && value.Length > 0 ? value : null;
    }

    public async Task<string> CurrentBranchRefAsync(string root, CancellationToken ct)
    {
        var result = await Run(root, ["symbolic-ref", "--quiet", "HEAD"], ct, allowFailure: true);
        return result.ExitCode == 0 ? result.StdOut.Trim() : "";
    }

    public string OriginalBranch(JournalEntry entry) =>
        entry.Steps.FirstOrDefault(step => step.Kind == "original")?.Target ?? "";

    public async Task SwitchToAsync(string root, string branch, CancellationToken ct, bool required)
    {
        if (branch.Length == 0)
            return;
        var name = StackRefs.Short(branch);
        if (string.Equals(await CurrentBranchRefAsync(root, ct), "refs/heads/" + name, StringComparison.Ordinal))
            return;
        var result = await Run(root, ["checkout", name], ct, allowFailure: true);
        if (result.ExitCode != 0 && required)
            throw new StackerException($"The branches are in place, but returning to {name} failed: {result.StdErr.Trim()}");
    }

    public RestackOutcome Refused(string message, JournalEntry? entry = null) => new(RestackStatus.Refused, null, [], message, entry);

    public string[] RebaseArgs(RestackPlan plan, RestackStep step)
    {
        var args = new List<string> { "rebase", "--onto", step.ParentRef, step.ReplayBase ?? step.OldParentTip!, step.BranchName };
        if (plan.PreserveMerges)
            args.Add("--rebase-merges");
        args.AddRange(["--no-autostash", "--no-autosquash", "--empty=drop"]);
        return [.. args];
    }

    public string[] AbortArgs(WorkingOperation operation) => operation switch
    {
        WorkingOperation.Merge => ["merge", "--abort"],
        WorkingOperation.CherryPick => ["cherry-pick", "--abort"],
        WorkingOperation.Revert => ["revert", "--abort"],
        WorkingOperation.Am => ["am", "--abort"],
        _ => ["rebase", "--abort"]
    };

    public async Task<ProcessResult> Run(string root, string[] args, CancellationToken ct, bool allowFailure = false, TimeSpan? timeout = null, string? editor = null)
    {
        var environment = editor is null
            ? WriteEnvironment
            : new Dictionary<string, string>(WriteEnvironment) { ["GIT_EDITOR"] = editor };
        var result = (await runner.RunAsync(new(executable.Resolve(),
            ["-c", "core.hooksPath=" + Framework.Neutral.HooksPath, "-c", "commit.gpgSign=false", "-c", "rebase.updateRefs=false", .. args],
            root, timeout ?? WriteTimeout, MaxOutputBytes: 4 * 1024 * 1024, Environment: environment), ct)).OrThrow();
        if (!allowFailure && result.ExitCode != 0)
            throw new StackerException($"Git exited with code {result.ExitCode}: {result.StdErr.Trim()}");
        return result;
    }

    public Task<ProcessResult> RunUser(string root, string[] args, CancellationToken ct) =>
        Boundary.UnwrapAsync(runner.RunAsync(new(executable.Resolve(), args, root, WriteTimeout, MaxOutputBytes: 4 * 1024 * 1024, Environment: WriteEnvironment), ct));

    public int ParseCount(string value) => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
