using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Supplies the recorded pull-request snapshot of an offline demo repository.</summary>
public interface IDemoSnapshotReader
{
    /// <summary>Reads the offline snapshot stored inside a demo repository.</summary>
    /// <param name="root">Root of the repository being opened.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The recorded snapshot, or <see langword="null"/> when this reader cannot supply demo data at all.</returns>
    /// <remarks>Used instead of a live GitHub read while a demo is open. A demo repository whose snapshot document is
    /// missing is a failure rather than an empty result, because the demo cannot work without it; the caller reports
    /// the missing snapshot instead of showing an empty PR list.</remarks>
    Task<Result<GitHubSnapshot?>> ReadDemoSnapshotAsync(string root, CancellationToken ct = default);
}
