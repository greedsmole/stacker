using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Tests;

public sealed partial class ClassDiagramUiTests
{
    private sealed class DiagramReader : ICSharpGraphReader
    {
        private TaskCompletionSource? _entered;
        private TaskCompletionSource? _release;
        private bool _block;
        public List<CSharpGraphRequest> Requests { get; } = [];
        public Task WaitForBlockedRequest => _entered!.Task;
        public bool BlockedRequestReturned { get; private set; }

        public void BlockNext()
        {
            _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _block = true;
        }

        public void ReleaseBlockedRequest() => _release!.SetResult();

        public Task<Result<IReadOnlyList<string>>> EntriesAsync(RepositoryIdentity repository, string commit, CancellationToken ct) =>
            Task.FromResult(Result<IReadOnlyList<string>>.Ok(["Stack.slnx", "Client/Client.csproj"]));

        public async Task<Result<CSharpGraph>> ReadAsync(CSharpGraphRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (_block)
            {
                _block = false;
                _entered!.SetResult();
                await _release!.Task;
                BlockedRequestReturned = true;
            }
            SymbolLocation[] locations = [new("A.txt", 1, 0)];
            CSharpType[] types =
            [
                new("caller", "Client/Client.csproj", "Example", "Example.Caller", "class", "type", locations,
                    [new("call", "public string Run()", request.Commit, locations), new("name", "private string name", "field", locations)]),
                new("catalog", "Library/Library.csproj", "Example", "Example.Catalog", "class", "type", locations,
                    [new("load", "public string Load()", "method", locations)])
            ];
            CSharpRelationship[] edges = [new("caller", "call", "catalog", "load", "Example.Catalog.Load()", CSharpRelationshipKind.Calls)];
            return Result<CSharpGraph>.Ok(new(request.Commit, types, edges, ["Client/Client.csproj", "Library/Library.csproj"], []));
        }
    }
}
