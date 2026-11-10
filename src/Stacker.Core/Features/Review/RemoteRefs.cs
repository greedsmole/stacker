namespace Stacker.Core;

/// <summary>Names the synthetic refs that project a pull request's head and base into a local repository snapshot.</summary>
/// <remarks>
/// Comparing a pull request stack reuses the local diff and comparison pipeline, which resolves layers by ref name against
/// a <see cref="RepositorySnapshot"/>. These names are the labels that snapshot carries for a pull request: the snapshot's
/// entry for <see cref="HeadRef"/> resolves to the pull request head commit and the entry for <see cref="BaseRef"/> to its
/// base commit, so the existing stack and diff code can show a remote stack without a second comparison implementation.
/// The names are never written to Git — the objects themselves are fetched into application-owned refs under
/// <c>refs/stacker/pr/...</c> — so creating one cannot collide with a real branch. They must still begin with
/// <c>refs/heads/</c> because the snapshot validators and the stack layer type accept only local or remote-tracking refs,
/// and the double underscore marks the name as internal in status text and diagnostics. The pull request number makes the
/// pair unique per pull request, and the same number appears in both names so a layer's head and base can be recognized as
/// belonging together.
/// </remarks>
public static class RemoteRefs
{
    /// <summary>The snapshot ref name that resolves to a pull request's head commit.</summary>
    /// <param name="pr">Pull request whose number names the ref.</param>
    /// <returns>A display-only ref name of the form <c>refs/heads/__pr_&lt;number&gt;</c>.</returns>
    public static string HeadRef(PullRequest pr) => $"refs/heads/__pr_{pr.Number}";
    /// <summary>The snapshot ref name that resolves to a pull request's base commit.</summary>
    /// <remarks>The projected snapshot needs a name for the stack base as well, and that base must resolve to the pull
    /// request's server-reported base commit rather than to a local branch that may be stale, so it gets its own
    /// per-pull-request label instead of reusing the branch name.</remarks>
    /// <param name="pr">Pull request whose number names the ref.</param>
    /// <returns>A display-only ref name of the form <c>refs/heads/__base_&lt;number&gt;</c>.</returns>
    public static string BaseRef(PullRequest pr) => $"refs/heads/__base_{pr.Number}";
}
