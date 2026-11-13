using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class FoundationConcurrencyTests
{
    private sealed class GatedReader : IGitRepositoryReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<Result<FileDiff>> PatchAsync(string root, string basis, string head, FileChange file, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            await Release.Task;
            return Result<FileDiff>.Ok(new(file, "", [], []));
        }
        public Task<Result<RepositorySnapshot>> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<string>>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<(int Ahead, int Behind)>> CountsAsync(string root, string parent, string head, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<FileChange>>> FilesAsync(string root, string basis, string head, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
