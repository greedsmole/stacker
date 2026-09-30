namespace Stacker.Core;

/// <summary>User-authored ordered description of a stack; branch names are full Git refs.</summary>
public sealed record StackDefinition(string Id, string Name, string Base, IReadOnlyList<string> Branches);
/// <summary>A named ref resolved to its immutable commit ID in one repository snapshot.</summary>
public sealed record BranchRef(string Name, string Sha)
{
    public string DisplayName => Name.Replace("refs/heads/", "").Replace("refs/remotes/", "");
    public override string ToString() => DisplayName;
}
/// <summary>Immutable view of the repository refs and working-tree summary used by a comparison.</summary>
public sealed record RepositorySnapshot(string Root, IReadOnlyList<BranchRef> Refs, int ChangedFiles);
/// <summary>Resolved state for one ordered layer, including parent relationship and divergence warning.</summary>
public sealed record StackLayerSnapshot(int Position, string Branch, string Parent, string? HeadSha,
    string? ParentSha, int Ahead, int Behind, string? Warning);
public enum DiffMode { Layer, Cumulative, FullStack, MultiLayer }
/// <summary>Requests one or more layer comparisons against a consistent repository and stack snapshot.</summary>
public sealed record DiffRequest(RepositorySnapshot Repository, StackDefinition Stack, DiffMode Mode, IReadOnlyList<int> Positions);
/// <summary>Diff metadata and file list for one layer; BaseSha and HeadSha record the exact compared objects.</summary>
public sealed record DiffResult(string Layer, string ParentRef, string BaseSha, string HeadSha,
    string? Warning, IReadOnlyList<FileChange> Files)
{
    public int Additions => Files.Sum(f => f.Additions ?? 0);
    public int Deletions => Files.Sum(f => f.Deletions ?? 0);
}
/// <summary>Git's file-level change classification, including renames and mode changes.</summary>
public enum FileChangeKind { Added, Modified, Deleted, Renamed, Copied, TypeChanged }
/// <summary>File metadata returned before an individual patch is requested.</summary>
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
/// <summary>Semantic role of a rendered patch line; line numbers are null on the side where it does not exist.</summary>
public enum DiffLineKind { Context, Added, Removed, Header, Notice }
public sealed record DiffLine(DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text);
/// <summary>A patch hunk and its parsed line coordinates, used to place inline review anchors.</summary>
public sealed record DiffHunk(string Header, IReadOnlyList<DiffLine> Lines, int OldStart = 0, int OldCount = 0, int NewStart = 0, int NewCount = 0, string Context = "");
/// <summary>Parsed and raw forms of a single file patch.</summary>
public sealed record FileDiff(FileChange File, string Patch, IReadOnlyList<DiffHunk> Hunks, IReadOnlyList<DiffLine> Lines);
/// <summary>Definitions plus the content revision used to prevent overwriting external edits.</summary>
public sealed record StackDocument(IReadOnlyList<StackDefinition> Stacks, string? Revision);
/// <summary>Complete execution policy for one external process, including resource limits and isolated environment edits.</summary>
public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory = null,
    TimeSpan? Timeout = null, int MaxOutputBytes = 16 * 1024 * 1024,
    IReadOnlyDictionary<string, string>? Environment = null, string? StandardInput = null, IReadOnlyList<string>? UnsetEnvironment = null);
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
/// <summary>Expected user-facing failure that can be shown without treating it as an application crash.</summary>
public class StackerException(string message) : Exception(message);
public sealed class GitHubAuthenticationException(string message) : StackerException(message);
public sealed class OutputLimitException() : StackerException("Output exceeds the display limit. Select a smaller file or comparison.");

public sealed class AppSettings
{
    /// <summary>Most recently opened local repository paths, ordered newest first by the UI.</summary>
    public List<string> RecentRepositories { get; set; } = [];
    public bool BackgroundRefresh { get; set; } = true;
    public bool WrapCode { get; set; }
    public string? LastRepository { get; set; }
    public string? GitPath { get; set; }
    public string? GhPath { get; set; }
    public bool UseSavedGhCredentials { get; set; }
    public string Theme { get; set; } = "Dark";
    public double NavigationWidth { get; set; } = 270;
    public double FilesWidth { get; set; } = 290;
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 880;
}
