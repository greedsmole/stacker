namespace Stacker.Core;

/// <summary>Defines the process boundary used by Git and GitHub CLI adapters.</summary>
/// <remarks>Keeping arguments separate from the executable prevents shell interpretation and makes cancellation testable.</remarks>
public interface IProcessRunner
{
    /// <summary>Runs one bounded process operation and returns both output streams and its exit code.</summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default);
}
/// <summary>Reads repository history and file content without changing refs, the index, or the working tree.</summary>
public interface IGitRepositoryReader
{
    /// <summary>Opens a repository and captures its refs and working-tree change count.</summary>
    Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default);
    /// <summary>Returns every best common ancestor; callers reject zero or ambiguous results.</summary>
    Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default);
    /// <summary>Counts commits unique to each side relative to the proposed parent/head pair.</summary>
    Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default);
    /// <summary>Reads machine-parsed file metadata for a fixed pair of commit IDs.</summary>
    Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default);
    /// <summary>Loads the patch for one previously listed file, subject to the display-size limit.</summary>
    Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default);
}
/// <summary>Persists user-authored stack definitions in the repository configuration.</summary>
public interface IStackStore
{
    /// <summary>Loads and validates definitions while capturing a revision for optimistic concurrency.</summary>
    Task<StackDocument> LoadAsync(string root, CancellationToken ct = default);
    /// <summary>Saves only when the on-disk revision still matches the revision last read.</summary>
    Task<StackDocument> SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct = default);
}
/// <summary>Reads source blobs by immutable commit and path for syntax-aware diff rendering.</summary>
public interface IBlobReader
{
    Task<BlobContent?> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default);
}
/// <summary>Stores per-user application preferences outside the repository.</summary>
public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
