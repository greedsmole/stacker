using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;

namespace Stacker.Tests;

public sealed partial class StateMigrationTests
{
    private sealed class RecordingState : IApplicationState
    {
        private readonly MemoryApplicationState _inner = new();
        public List<(string Kind, string Identity)> Writes { get; } = [];
        public string Root => "test";
        public string? LastProblem => null;
        public bool FailStackWrite
        {
            get; set;
        }
        public Task<Result<T?>> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class => _inner.ReadAsync<T>(kind, identity, ct);
        public Task<Result<Unit>> WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class
        {
            Writes.Add((kind, identity));
            return FailStackWrite && kind == "workspace-stacks"
                ? Task.FromResult(Result<Unit>.Fail(ProblemCodes.StoreWrite, "disk full"))
                : _inner.WriteAsync(kind, identity, value, ct);
        }
    }
}
