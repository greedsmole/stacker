using Stacker.Core.Framework;

namespace Stacker.Core;

public interface IWorkingTreeAuthoring
{
    Task<Result<WorkingTreeSnapshot>> ReadAsync(string root, CancellationToken ct = default);
    Task<Result<FileDiff>> ReadPatchAsync(WorkingTreeSnapshot snapshot, WorkingTreeFile file, CancellationToken ct = default);
    Task<Result<Unit>> UpdateIndexAsync(WorkingTreeSnapshot snapshot, WorkingTreeSide side, IReadOnlyList<WorkingTreeFile> files,
        CancellationToken ct = default);
    Task<Result<string>> CommitStagedAsync(WorkingTreeSnapshot snapshot, string message, CancellationToken ct = default);
}
