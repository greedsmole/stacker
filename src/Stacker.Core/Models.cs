namespace Stacker.Core;

public sealed record StackDefinition(string Id, string Name, string Base, IReadOnlyList<string> Branches);
public sealed record BranchRef(string Name, string Sha)
{
    public string DisplayName => Name.Replace("refs/heads/", "").Replace("refs/remotes/", "");
    public override string ToString() => DisplayName;
}
public sealed record RepositorySnapshot(string Root, IReadOnlyList<BranchRef> Refs, int ChangedFiles);
public sealed record StackLayerSnapshot(int Position, string Branch, string Parent, string? HeadSha,
    string? ParentSha, int Ahead, int Behind, string? Warning);
public enum DiffMode { Layer, Cumulative, FullStack, MultiLayer }
public sealed record DiffRequest(RepositorySnapshot Repository, StackDefinition Stack, DiffMode Mode, IReadOnlyList<int> Positions);
public sealed record DiffResult(string Layer, string ParentRef, string BaseSha, string HeadSha,
    string? Warning, IReadOnlyList<FileChange> Files)
{
    public int Additions => Files.Sum(f => f.Additions ?? 0);
    public int Deletions => Files.Sum(f => f.Deletions ?? 0);
}
public enum FileChangeKind { Added, Modified, Deleted, Renamed, Copied, TypeChanged }
public sealed record FileChange(string OldPath, string NewPath, FileChangeKind Kind,
    int? Additions, int? Deletions, string OldMode, string NewMode)
{
    public bool IsBinary => Additions is null;
    public bool IsSubmodule => OldMode == "160000" || NewMode == "160000";
    public bool ModeChanged => OldMode != NewMode && OldMode != "000000" && NewMode != "000000";
    public string DisplayPath => OldPath == NewPath ? NewPath : $"{OldPath} → {NewPath}";
    public string Stats => IsBinary ? "binary" : $"+{Additions}  −{Deletions}";
    public string Detail => IsSubmodule ? "Submodule" : ModeChanged ? $"Mode {OldMode} → {NewMode}" : Kind.ToString();
}
public enum DiffLineKind { Context, Added, Removed, Header, Notice }
public sealed record DiffLine(DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text);
public sealed record DiffHunk(string Header, IReadOnlyList<DiffLine> Lines);
public sealed record FileDiff(FileChange File, string Patch, IReadOnlyList<DiffHunk> Hunks, IReadOnlyList<DiffLine> Lines);
public sealed record StackDocument(IReadOnlyList<StackDefinition> Stacks, string? Revision);
public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory = null,
    TimeSpan? Timeout = null, int MaxOutputBytes = 16 * 1024 * 1024,
    IReadOnlyDictionary<string, string>? Environment = null, string? StandardInput = null, IReadOnlyList<string>? UnsetEnvironment = null);
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
public class StackerException(string message) : Exception(message);
public sealed class OutputLimitException() : StackerException("Output exceeds the display limit. Select a smaller file or comparison.");

public sealed class AppSettings
{
    public List<string> RecentRepositories { get; set; } = [];
    public string? GitPath { get; set; }
    public string? GhPath { get; set; }
    public bool UseSavedGhCredentials { get; set; }
    public string Theme { get; set; } = "Dark";
    public double NavigationWidth { get; set; } = 270;
    public double FilesWidth { get; set; } = 290;
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 880;
}
