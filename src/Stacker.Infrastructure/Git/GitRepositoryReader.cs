// Purpose: Read-only Git adapter that snapshots refs, calculates ancestry/file metadata, and loads bounded patches and source blobs.
using System.Globalization;
using System.Text.RegularExpressions;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

public sealed class GitRepositoryReader(IProcessRunner runner, GitExecutable executable, IChangeIdentityService? identities = null) : IGitRepositoryReader, IBlobReader, IDemoSnapshotReader
{
    public async Task<GitHubSnapshot?> ReadDemoSnapshotAsync(string root, CancellationToken ct = default) =>
        System.Text.Json.JsonSerializer.Deserialize<GitHubSnapshot>(await File.ReadAllTextAsync(Path.Combine(root, ".stacker-demo.json"), ct));
    // These per-process settings keep interactive prompts, pagers, optional index writes, and locale-dependent parsing out of reads.
    private static readonly IReadOnlyDictionary<string, string> EnvironmentOverrides = new Dictionary<string, string>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "Never",
        ["GIT_PAGER"] = "cat",
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GIT_NO_LAZY_FETCH"] = "1",
        ["GIT_LITERAL_PATHSPECS"] = "1",
        ["LC_ALL"] = "C"
    };
    // Reads on a large repository or a network mount routinely exceed the process default; give them a real budget.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);
    /// <summary>Runs a non-interactive Git read and converts unexpected exit codes into an actionable UI error.</summary>
    private async Task<ProcessResult> Run(string root, string[] args, CancellationToken ct, int limit = 16 * 1024 * 1024, bool allowOne = false)
    {
        var result = (await runner.RunAsync(new(executable.Resolve(),
            ["--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-c", "core.quotePath=false", .. args],
            root, ReadTimeout, MaxOutputBytes: limit, Environment: EnvironmentOverrides), ct)).OrThrow();
        if (result.ExitCode != 0 && !(allowOne && result.ExitCode == 1))
            throw new StackerException($"Git exited with code {result.ExitCode}: {result.StdErr.Trim()}");
        return result;
    }
    /// <summary>Captures only direct local and remote-tracking refs plus the working-tree changed-file count.</summary>
    public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default) => OpenAsync(path, includeStatus: true, ct);

    private async Task<RepositorySnapshot> OpenAsync(string path, bool includeStatus, CancellationToken ct)
    {
        if (!Directory.Exists(path))
            throw new StackerException("The repository folder does not exist.");
        var identity = identities is null ? null : (await identities.RegisterAsync(path, ct)).OrThrow();
        var root = identity?.Root ?? (await Run(path, ["rev-parse", "--show-toplevel"], ct)).StdOut.TrimEnd('\r', '\n');
        var output = (await Run(root, ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(symref)%00", "refs/heads", "refs/remotes"], ct)).StdOut;
        var refs = new List<BranchRef>();
        var fields = output.Split('\0');
        for (var i = 0; i + 2 < fields.Length; i += 3)
            if (fields[i + 2].Length == 0)
                refs.Add(new(fields[i].TrimStart('\n', '\r'), fields[i + 1]));
        var changed = includeStatus
            ? WorkingTreeParser.Parse((await Run(root, ["status", "--porcelain=v1", "-z", "--untracked-files=normal"], ct)).StdOut).Count : 0;
        if (identity is null)
        {
            var common = (await Run(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct)).StdOut.TrimEnd('\r', '\n');
            identity = new(new(StableIdentity.For(common)[..32]), new(StableIdentity.For(root)[..32]), root, common);
        }
        return new RepositorySnapshot(root, refs, changed) { Identity = identity, IsDemo = File.Exists(Path.Combine(root, ".stacker-demo.json")) };
    }

    Task<Result<RepositorySnapshot>> IGitRepositoryReader.OpenRefsAsync(string path, CancellationToken ct) =>
        Boundary.CaptureAsync(() => OpenAsync(path, includeStatus: false, ct), ProblemCodes.GitFailed);
    /// <summary>Returns all merge bases because more than one best ancestor makes the comparison ambiguous.</summary>
    public async Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default)
    {
        Sha(left);
        Sha(right);
        return (await Run(root, ["merge-base", "--all", left, right], ct, allowOne: true)).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    /// <summary>Counts commits exclusive to the head and parent, used to show layer divergence rather than merge status.</summary>
    public async Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default)
    {
        Sha(parent);
        Sha(head);
        var values = (await Run(root, ["rev-list", "--left-right", "--count", $"{parent}...{head}"], ct)).StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(values[1], CultureInfo.InvariantCulture), int.Parse(values[0], CultureInfo.InvariantCulture));
    }
    private static readonly string[] PatchOptions =
        ["--no-color", "--no-ext-diff", "--no-textconv", "--ignore-submodules=none", "--submodule=short", "--no-relative", "--src-prefix=a/", "--dst-prefix=b/"];

    private static string[] DiffArgs(string @base, string head) => ["diff", .. PatchOptions, "--find-renames=50%", "-l1000", @base, head];
    // Git emits raw records first, then numstat. The same invocation does rename detection for both.
    public async Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default)
    {
        Sha(@base);
        Sha(head);
        var records = (await Run(root, [.. DiffArgs(@base, head), "--raw", "--numstat", "--no-abbrev", "-z", "--"],
            ct, 32 * 1024 * 1024)).StdOut.Split('\0');
        var files = new List<FileChange>();
        var index = 0;
        while (index < records.Length && records[index].StartsWith(':'))
        {
            var header = records[index++].Split(' ');
            var code = header[4][0];
            var oldPath = records[index++];
            var newPath = code is 'R' or 'C' ? records[index++] : oldPath;
            var kind = code switch
            {
                'A' => FileChangeKind.Added,
                'D' => FileChangeKind.Deleted,
                'R' => FileChangeKind.Renamed,
                'C' => FileChangeKind.Copied,
                'T' => FileChangeKind.TypeChanged,
                _ => FileChangeKind.Modified
            };
            files.Add(new(oldPath, newPath, kind, 0, 0, header[0][1..], header[1]));
        }
        var counts = new Dictionary<(string, string), (int? Added, int? Deleted)>();
        while (index < records.Length && records[index].Length > 0)
        {
            var record = records[index++].Split('\t', 3);
            var oldPath = record[2];
            var newPath = oldPath;
            if (oldPath.Length == 0)
            {
                oldPath = records[index++];
                newPath = records[index++];
            }
            counts[(oldPath, newPath)] = (ParseCount(record[0]), ParseCount(record[1]));
        }
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (counts.TryGetValue((file.OldPath, file.NewPath), out var count))
                files[i] = file with { Additions = count.Added, Deletions = count.Deleted };
        }
        return files;
    }
    /// <summary>Loads a single file patch only after the file list has been shown, keeping large diffs lazy.</summary>
    public async Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default)
    {
        Sha(@base);
        Sha(head);
        var paths = file.OldPath == file.NewPath ? new[] { file.NewPath } : [file.OldPath, file.NewPath];
        var patch = (await Run(root, ["-c", "diff.suppressBlankEmpty=false", .. DiffArgs(@base, head), "--patch", "--unified=3", "--", .. paths], ct, 5 * 1024 * 1024)).StdOut;
        // A rename's old path may have been reused by another file. Path filtering can
        // therefore return multiple patches; retain only this exact file pair.
        patch = SelectFilePatch(patch, file.OldPath, file.NewPath)
            ?? throw new StackerException("Git returned an unexpected patch header. Refresh the comparison.");
        if (patch.Count(c => c == '\n') > DiffView.DisplayLineLimit)
            throw new OutputLimitException();
        return PatchParser.Parse(file, patch);
    }

    internal async Task<FileDiff> WorkingTreePatchAsync(string root, WorkingTreeFile selected, CancellationToken ct)
    {
        string[] args = selected.IsUntracked
            ? ["diff", "--no-index", .. PatchOptions, "--patch", "--unified=3", "--", "/dev/null", "./" + selected.Path]
            : ["diff", .. PatchOptions, .. (selected.Side == WorkingTreeSide.Staged ? new[] { "--cached" } : []),
                "--find-renames=50%", "-l1000", "--patch", "--unified=3", "--",
                .. (selected.OldPath == selected.Path ? new[] { selected.Path } : [selected.OldPath, selected.Path])];
        var patch = (await Run(root, ["-c", "diff.suppressBlankEmpty=false", .. args], ct, 5 * 1024 * 1024, allowOne: selected.IsUntracked)).StdOut;
        if (!selected.IsUntracked && patch.Length > 0)
            patch = SelectFilePatch(patch, selected.OldPath, selected.Path)
                ?? throw new StackerException("This file changed on disk. Refresh the working tree and try again.");
        if (patch.Count(c => c == '\n') > DiffView.DisplayLineLimit)
            throw new OutputLimitException();

        var kind = selected.Status switch
        {
            'A' or '?' => FileChangeKind.Added,
            'D' => FileChangeKind.Deleted,
            'R' => FileChangeKind.Renamed,
            'C' => FileChangeKind.Copied,
            'T' => FileChangeKind.TypeChanged,
            _ => FileChangeKind.Modified
        };
        var headers = patch.Split('\n').TakeWhile(line => !line.StartsWith("@@", StringComparison.Ordinal)).ToArray();
        var binary = headers.Any(line => line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch");
        var submodule = headers.Any(line => line.EndsWith(" 160000", StringComparison.Ordinal));
        var file = new FileChange(selected.OldPath, selected.Path, kind, binary ? null : 0, binary ? null : 0,
            submodule ? "160000" : "100644", submodule ? "160000" : "100644");
        var diff = PatchParser.Parse(file, patch);
        return binary ? diff : diff with
        {
            File = file with
            {
                Additions = diff.Lines.Count(line => line.Kind == DiffLineKind.Added),
                Deletions = diff.Lines.Count(line => line.Kind == DiffLineKind.Removed)
            }
        };
    }

    private static string? SelectFilePatch(string patch, string oldPath, string newPath)
    {
        var expected = $"diff --git {QuotePath("a/" + oldPath)} {QuotePath("b/" + newPath)}\n";
        return Regex.Split(patch, @"(?m)(?=^diff --git )")
            .FirstOrDefault(section => section.StartsWith(expected, StringComparison.Ordinal));
    }
    /// <summary>Reads immutable blob content for syntax highlighting; absent old/new blobs are normal for add/delete.</summary>
    public async Task<BlobContent?> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default)
    {
        Sha(commit);
        var objectName = commit + ":" + path;
        // Missing paths are normal for added/deleted files. Nonzero lookup is not a renderer error, but the call still
        // goes through the hardened wrapper so a partial clone cannot lazily fetch or prompt while highlighting.
        var exists = await Run(root, ["rev-parse", "--verify", "--quiet", objectName], ct, allowOne: true);
        if (exists.ExitCode != 0)
            return null;
        var blob = exists.StdOut.Trim();
        Sha(blob);
        try
        {
            return new(blob, (await Run(root, ["cat-file", "blob", blob], ct, 2 * 1024 * 1024)).StdOut);
        }
        catch (OutputLimitException) { return null; }
    }
    internal async Task RequireCleanCheckoutAsync(string root, string commit, CancellationToken ct)
    {
        Sha(commit);
        var head = (await Run(root, ["rev-parse", "--verify", "HEAD"], ct)).StdOut.Trim();
        if (!string.Equals(head, commit, StringComparison.OrdinalIgnoreCase))
            throw new StackerException($"C# navigation requires checking out {commit[..8]}, the commit for this side of the diff.");
        var changes = (await Run(root, ["status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=none"], ct)).StdOut;
        if (changes.Length > 0)
            throw new StackerException("C# navigation requires a clean checkout. Commit or stash local changes and retry.");
    }

    internal async Task<IReadOnlyList<string>> TrackedPathsAsync(string root, string commit, CancellationToken ct)
    {
        Sha(commit);
        var tree = await Run(root, ["ls-tree", "-r", "--name-only", "-z", commit], ct);
        return tree.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
    private static string QuotePath(string path)
    {
        if (!path.Any(c => c < 32 || c == 127 || c is '"' or '\\'))
            return path;
        var quoted = new System.Text.StringBuilder("\"");
        foreach (var c in path)
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\a' => "\\a",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\v' => "\\v",
                '\f' => "\\f",
                '\r' => "\\r",
                < (char)32 or (char)127 => "\\" + Convert.ToString(c, 8).PadLeft(3, '0'),
                _ => c.ToString()
            });
        return quoted.Append('"').ToString();
    }
    private static int? ParseCount(string value) => value == "-" ? null : int.Parse(value, CultureInfo.InvariantCulture);
    private static void Sha(string value)
    {
        if (!Regex.IsMatch(value, "\\A(?:[0-9a-f]{40}|[0-9a-f]{64})\\z"))
            throw new StackerException("Invalid commit ID. Refresh the repository.");
    }

    Task<Result<RepositorySnapshot>> IGitRepositoryReader.OpenAsync(string path, CancellationToken ct) => Boundary.CaptureAsync(() => OpenAsync(path, ct), ProblemCodes.GitFailed);

    Task<Result<IReadOnlyList<string>>> IGitRepositoryReader.MergeBasesAsync(string root, string left, string right, CancellationToken ct) => Boundary.CaptureAsync(() => MergeBasesAsync(root, left, right, ct), ProblemCodes.GitFailed);

    Task<Result<(int Ahead, int Behind)>> IGitRepositoryReader.CountsAsync(string root, string parent, string head, CancellationToken ct) => Boundary.CaptureAsync(() => CountsAsync(root, parent, head, ct), ProblemCodes.GitFailed);

    Task<Result<IReadOnlyList<FileChange>>> IGitRepositoryReader.FilesAsync(string root, string @base, string head, CancellationToken ct) => Boundary.CaptureAsync(() => FilesAsync(root, @base, head, ct), ProblemCodes.GitFailed);

    Task<Result<FileDiff>> IGitRepositoryReader.PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct) => Boundary.CaptureAsync(() => PatchAsync(root, @base, head, file, ct), ProblemCodes.GitFailed);

    Task<Result<BlobContent?>> IBlobReader.ReadBlobAsync(string root, string commit, string path, CancellationToken ct) => Boundary.CaptureAsync(() => ReadBlobAsync(root, commit, path, ct), ProblemCodes.GitFailed);

    Task<Result<GitHubSnapshot?>> IDemoSnapshotReader.ReadDemoSnapshotAsync(string root, CancellationToken ct) => Boundary.CaptureAsync(() => ReadDemoSnapshotAsync(root, ct), ProblemCodes.GitFailed);
}
