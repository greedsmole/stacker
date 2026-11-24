using Stacker.Core.Framework;

namespace Stacker.Core;

public interface IGhStackClient
{
    Task<Result<GhStackTool>> ProbeAsync(GhStackTarget target, CancellationToken ct = default);
    Task<Result<GhStackView>> LoadAsync(GhStackTarget target, CancellationToken ct = default);
    Task<Result<GhStackRebasePlan>> PreviewRebaseAsync(GhStackTarget target, CancellationToken ct = default);
    Task<Result<GhStackRecovery?>> RecoveryAsync(string root, CancellationToken ct = default);
    Task<CommandOutcome> CreateAsync(GhStackTarget target, string trunk, string branch, CancellationToken ct = default);
    Task<CommandOutcome> AddAsync(GhStackTarget target, string branch, CancellationToken ct = default);
    Task<CommandOutcome> RebaseAsync(GhStackRebasePlan plan, CancellationToken ct = default);
    Task<CommandOutcome> RecoverAsync(GhStackTarget target, string operationId, bool abort, CancellationToken ct = default);
    Task<CommandOutcome> AcknowledgeAsync(string root, string operationId, CancellationToken ct = default);
}
