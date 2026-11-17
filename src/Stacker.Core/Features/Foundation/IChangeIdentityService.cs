using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Resolves and persists repository and worktree identities, and binds a local change to its GitHub counterpart.</summary>
/// <remarks>Identity is deliberately independent of folder paths: registration resolves the Git common directory and
/// working-tree root, relocation preserves both ids, and a binding requires the verified account plus a matching local
/// ref. Every guard exists so a second clone or a second account cannot inherit another history by accident. Methods
/// return their outcome instead of throwing; cancellation is the only expected exception.</remarks>
public interface IChangeIdentityService
{
    /// <summary>Returns every registered repository location, ordered by root path.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>An empty list when nothing was ever registered, otherwise one entry per registered worktree; a failed
    /// result carries the store's read code.</returns>
    Task<Result<IReadOnlyList<RepositoryIdentity>>> KnownAsync(CancellationToken ct = default);
    /// <summary>Registers the repository that contains <paramref name="root"/> and returns its identity.</summary>
    /// <param name="root">Folder inside the repository or worktree to register; Git resolves the real root.</param>
    /// <param name="ct">Cancels the Git resolution and the database write.</param>
    /// <returns>The already registered identity when the common directory or root was seen before, otherwise a newly
    /// created one.</returns>
    /// <remarks>Fails instead of guessing when Git cannot resolve the location unambiguously, for example outside a
    /// repository or with an unexpected <c>rev-parse</c> layout. It never hands an existing identity to a different
    /// repository, which is the guard that keeps a second clone from merging into the first one's history.</remarks>
    Task<Result<RepositoryIdentity>> RegisterAsync(string root, CancellationToken ct = default);
    /// <summary>Associates a new root with an existing repository identity, keeping the repository and worktree ids.</summary>
    /// <param name="repository">Identity the caller already holds, typically from <see cref="KnownAsync"/>.</param>
    /// <param name="root">New location of that repository or worktree.</param>
    /// <param name="worktree">Worktree to keep, when the caller wants to pin a specific worktree instead of letting the
    /// location resolve one.</param>
    /// <param name="ct">Cancels the Git resolution and the database write.</param>
    /// <returns>The identity with the new root and the same ids.</returns>
    /// <remarks>Refuses when the target common directory already belongs to a different identity, when the requested
    /// repository id is unknown, when the requested worktree id is not registered for that repository, or when the root
    /// is already a different worktree. Each refusal keeps the stored history untouched rather than merging it. This is
    /// the explicit path for a moved folder; plain registration of an unrelated clone never merges.</remarks>
    Task<Result<RepositoryIdentity>> RelocateAsync(RepositoryId repository, string root, WorktreeId? worktree = null, CancellationToken ct = default);
    /// <summary>Binds one local change reference to a GitHub repository and pull request, and aliases the two change nodes.</summary>
    /// <param name="localScope">Local history scope that owns the local change, for example
    /// <see cref="RepositoryIdentity.Scope"/>.</param>
    /// <param name="localRef">Local branch ref, which must match the pull request's head ref.</param>
    /// <param name="context">Verified GitHub context of the active account and repository.</param>
    /// <param name="pr">Pull request snapshot to bind.</param>
    /// <param name="ct">Cancels the database write.</param>
    /// <returns>A successful empty result, or a failed result when either guard rejects the binding.</returns>
    /// <remarks>Guards: the pull request head must come from the same repository the context names, and the local ref
    /// must be <c>refs/heads/</c> plus the pull request's head branch; and the local scope must not already be bound to
    /// another account. On success the local change node is aliased to the remote one, so checkpoints and versions
    /// recorded locally are visible from the pull request review and the other way round.</remarks>
    Task<Result<Unit>> BindAsync(string localScope, string localRef, GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default);
}
