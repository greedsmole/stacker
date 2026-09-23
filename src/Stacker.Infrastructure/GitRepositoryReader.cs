using System.Globalization;
using System.Text.RegularExpressions;
using Stacker.Core;

namespace Stacker.Infrastructure;

public sealed class GitRepositoryReader(IProcessRunner runner, GitExecutable executable) : IGitRepositoryReader, IBlobReader
{
    private static readonly IReadOnlyDictionary<string, string> EnvironmentOverrides = new Dictionary<string, string>
    {
        ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "Never", ["GIT_PAGER"] = "cat",
        ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_NO_LAZY_FETCH"] = "1", ["GIT_LITERAL_PATHSPECS"] = "1",
        ["LC_ALL"] = "C"
    };
    private async Task<ProcessResult> Run(string root, string[] args, CancellationToken ct, int limit = 16 * 1024 * 1024, bool allowOne = false)
    {
        var result = await runner.RunAsync(new(executable.Resolve(),
            ["--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-c", "core.quotePath=false", .. args],
            root, MaxOutputBytes: limit, Environment: EnvironmentOverrides), ct);
        if (result.ExitCode != 0 && !(allowOne && result.ExitCode == 1))
            throw new StackerException($"Git exited with code {result.ExitCode}: {result.StdErr.Trim()}");
        return result;
    }
    public async Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) throw new StackerException("The repository folder does not exist.");
        var root = (await Run(path, ["rev-parse", "--show-toplevel"], ct)).StdOut.TrimEnd('\r', '\n');
        var output = (await Run(root, ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(symref)%00", "refs/heads", "refs/remotes"], ct)).StdOut;
        var refs = new List<BranchRef>();
        var fields = output.Split('\0');
        for (var i = 0; i + 2 < fields.Length; i += 3)
            if (fields[i + 2].Length == 0) refs.Add(new(fields[i].TrimStart('\n', '\r'), fields[i + 1]));
        var status = (await Run(root, ["status", "--porcelain=v1", "-z", "--untracked-files=normal"], ct)).StdOut.Split('\0');
        var changed = 0;
        for (var i = 0; i < status.Length; i++)
        {
            if (status[i].Length < 3) continue;
            changed++;
            if (status[i][0] is 'R' or 'C' || status[i][1] is 'R' or 'C') i++;
        }
        return new(root, refs, changed);
    }
    public async Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default)
    {
        Sha(left); Sha(right);
        return (await Run(root, ["merge-base", "--all", left, right], ct, allowOne: true)).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    public async Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default)
    {
        Sha(parent); Sha(head);
        var values = (await Run(root, ["rev-list", "--left-right", "--count", $"{parent}...{head}"], ct)).StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(values[1], CultureInfo.InvariantCulture), int.Parse(values[0], CultureInfo.InvariantCulture));
    }
    private static string[] DiffArgs(string @base, string head) => ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "--ignore-submodules=none", "--no-relative", "--src-prefix=a/", "--dst-prefix=b/", "--find-renames=50%", "-l1000", @base, head];
    public async Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default)
    {
        Sha(@base); Sha(head);
        var raw = (await Run(root, [.. DiffArgs(@base, head), "--raw", "--no-abbrev", "-z", "--"], ct)).StdOut.Split('\0');
        var stats = (await Run(root, [.. DiffArgs(@base, head), "--numstat", "-z", "--"], ct)).StdOut.Split('\0');
        var counts = new Dictionary<(string, string), (int?, int?)>();
        for (var i = 0; i < stats.Length && stats[i].Length > 0; i++)
        {
            var record = stats[i].Split('\t', 3);
            string oldPath, newPath;
            if (record[2].Length == 0) { oldPath = stats[++i]; newPath = stats[++i]; }
            else oldPath = newPath = record[2];
            counts[(oldPath, newPath)] = (ParseCount(record[0]), ParseCount(record[1]));
        }
        var files = new List<FileChange>();
        for (var i = 0; i < raw.Length && raw[i].Length > 0; i++)
        {
            var header = raw[i].Split(' ');
            var code = header[4][0];
            var oldPath = raw[++i];
            var newPath = code is 'R' or 'C' ? raw[++i] : oldPath;
            var count = counts.GetValueOrDefault((oldPath, newPath), (0, 0));
            var kind = code switch { 'A' => FileChangeKind.Added, 'D' => FileChangeKind.Deleted, 'R' => FileChangeKind.Renamed, 'C' => FileChangeKind.Copied, 'T' => FileChangeKind.TypeChanged, _ => FileChangeKind.Modified };
            files.Add(new(oldPath, newPath, kind, count.Item1, count.Item2, header[0][1..], header[1]));
        }
        return files;
    }
    public async Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default)
    {
        Sha(@base); Sha(head);
        var paths = file.OldPath == file.NewPath ? new[] { file.NewPath } : [file.OldPath, file.NewPath];
        var patch = (await Run(root, [.. DiffArgs(@base, head), "--patch", "--unified=3", "--", .. paths], ct, 5 * 1024 * 1024)).StdOut;
        // A rename's old path may have been reused by another file. Path filtering can
        // therefore return multiple patches; retain only this exact file pair.
        var expectedHeader = $"diff --git {QuotePath("a/" + file.OldPath)} {QuotePath("b/" + file.NewPath)}";
        var sections = Regex.Split(patch, @"(?m)(?=^diff --git )");
        patch = sections.FirstOrDefault(section => section.StartsWith(expectedHeader + "\n", StringComparison.Ordinal))
            ?? throw new StackerException("Git returned an unexpected patch header. Refresh the comparison.");
        if (patch.Count(c => c == '\n') > 50_000) throw new OutputLimitException();
        return PatchParser.Parse(file, patch);
    }
    public async Task<BlobContent?> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default)
    {
        Sha(commit);
        var objectName = commit + ":" + path;
        // Missing paths are normal for added/deleted files. Nonzero lookup is not a renderer error.
        var exists = await runner.RunAsync(new(executable.Resolve(), ["rev-parse", "--verify", objectName], root), ct);
        if (exists.ExitCode != 0) return null;
        var blob = exists.StdOut.Trim(); Sha(blob);
        try { return new(blob, (await Run(root, ["cat-file", "blob", blob], ct, 2 * 1024 * 1024)).StdOut); }
        catch (OutputLimitException) { return null; }
    }
    private static string QuotePath(string path)
    {
        if (!path.Any(c => c < 32 || c == 127 || c is '"' or '\\')) return path;
        var quoted = new System.Text.StringBuilder("\"");
        foreach (var c in path)
            quoted.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\a' => "\\a", '\b' => "\\b", '\t' => "\\t",
                '\n' => "\\n", '\v' => "\\v", '\f' => "\\f", '\r' => "\\r",
                < (char)32 or (char)127 => "\\" + Convert.ToString(c, 8).PadLeft(3, '0'),
                _ => c.ToString()
            });
        return quoted.Append('"').ToString();
    }
    private static int? ParseCount(string value) => value == "-" ? null : int.Parse(value, CultureInfo.InvariantCulture);
    private static void Sha(string value)
    {
        if (!Regex.IsMatch(value, "\\A(?:[0-9a-f]{40}|[0-9a-f]{64})\\z")) throw new StackerException("Invalid commit ID. Refresh the repository.");
    }
}
