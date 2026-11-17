namespace Stacker.Core;

/// <summary>Everything the workspace displays at one revision: the repository, the applied remote snapshot and its graph.</summary>
/// <param name="Revision">Monotonic revision of the displayed data, starting at 0. Each displayed change advances it by
/// one, and a staged snapshot carries the value that was reserved for it when it was offered.</param>
/// <param name="Repository">Repository snapshot in use, or <see langword="null"/> before a repository is opened.</param>
/// <param name="Remote">Applied GitHub snapshot, or <see langword="null"/> when no remote side is applied.</param>
/// <param name="Graph">History graph that belongs to <paramref name="Remote"/>, or <see langword="null"/>.</param>
/// <remarks>The record is immutable: a change replaces the whole value with a modified copy, so a reader that captured an
/// earlier instance keeps a consistent view. The revision is what query tickets compare, which is why an asynchronous
/// producer has to check the ticket before publishing anything derived from an older instance.</remarks>
public sealed record WorkspaceSnapshot(long Revision, RepositorySnapshot? Repository, GitHubSnapshot? Remote, GraphSnapshot? Graph);
