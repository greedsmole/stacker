using Stacker.Core.Framework;

namespace Stacker.Core;

public interface ICSharpGraphReader
{
    Task<Result<IReadOnlyList<string>>> EntriesAsync(RepositoryIdentity repository, string commit, CancellationToken ct);
    Task<Result<CSharpGraph>> ReadAsync(CSharpGraphRequest request, CancellationToken ct);
}
