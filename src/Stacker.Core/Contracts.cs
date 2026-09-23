namespace Stacker.Core;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default);
}
public interface IGitRepositoryReader
{
    Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default);
    Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default);
    Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default);
    Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default);
}
public interface IStackStore
{
    Task<StackDocument> LoadAsync(string root, CancellationToken ct = default);
    Task<StackDocument> SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct = default);
}
public interface IBlobReader
{
    Task<BlobContent?> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default);
}
public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
