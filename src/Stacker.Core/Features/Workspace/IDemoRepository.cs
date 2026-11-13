using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Creates a fresh offline demo repository so the workspace can be explored without a network account.</summary>
public interface IDemoRepository
{
    /// <summary>Builds a new demo repository and returns the root that should be opened.</summary>
    /// <param name="ct">Cancels generation, which removes the partially built demo before the failure propagates.</param>
    /// <returns>The root of the created repository.</returns>
    /// <remarks>Each call builds a uniquely named repository, so reopening the demo never overwrites work in a
    /// previous one. Older generated demos are pruned so repeated use cannot grow without bound: the new demo plus
    /// the two most recent earlier ones are kept. The demo is disposable, not a user repository.</remarks>
    Task<Result<string>> CreateAsync(CancellationToken ct = default);
}
