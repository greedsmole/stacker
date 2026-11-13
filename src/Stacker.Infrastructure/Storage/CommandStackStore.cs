using Stacker.Core;
using Stacker.Core.Features.Workspace;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed class CommandStackStore(YamlStackStore store, CommandBus bus) : IStackStore
{
    public Task<StackDocument> LoadAsync(string root, CancellationToken ct = default) => store.LoadAsync(root, ct);
    public async Task<StackDocument> SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct = default)
    {
        var repository = RepoRoot.Parse(root).OrThrow();
        var command = new SaveStackCommand(repository, async (_, token) =>
        {
            try
            {
                return Result<StackDocument>.Ok(await store.SaveAsync(root, stacks, expectedRevision, token));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Result<StackDocument>.Fail(ProblemCodes.StoreWrite, ex.Message); }
        });
        var outcome = await bus.RunAsync(repository, command, ct: ct);
        if (outcome.Problem is not null && command.Value is { IsOk: true })
            throw new StackerException(outcome.Message);
        return command.Value is { } value ? value.OrThrow() : throw new StackerException(outcome.Message);
    }

    Task<Result<StackDocument>> IStackStore.LoadAsync(string root, CancellationToken ct) => Boundary.CaptureAsync(() => LoadAsync(root, ct), ProblemCodes.StoreWrite);

    Task<Result<StackDocument>> IStackStore.SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct) => Boundary.CaptureAsync(() => SaveAsync(root, stacks, expectedRevision, ct), ProblemCodes.StoreWrite);
}
