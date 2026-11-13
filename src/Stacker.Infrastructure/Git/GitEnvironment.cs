using Stacker.Core;
namespace Stacker.Infrastructure;

/// <summary>Removes the Git environment variables that would point a command at a different repository, worktree, index or object store.</summary>
/// <remarks>Every command is given an explicit working directory, but Git also honours inherited variables such as
/// <c>GIT_DIR</c>, and those take precedence over discovery from that directory. A shell that exported one would silently
/// redirect reads, and worse ref writes, to another repository, so the redirects are removed before the child starts. Only
/// removal is performed - nothing is set - and the request's own removal list is kept. The policy is applied by the process
/// adapter to every command it recognizes as Git, including a configured custom binary, which is what makes the explicit
/// repository in the request authoritative; the redirect set and this policy are recorded in <c>docs/LOCAL_REPOSITORY.md</c>.</remarks>
internal static class GitEnvironment
{
    /// <summary>Variables that make Git read or write a different repository, worktree, index or object database than the explicit working directory.</summary>
    /// <remarks><c>GIT_DIR</c>, <c>GIT_WORK_TREE</c> and <c>GIT_COMMON_DIR</c> redirect the repository itself,
    /// <c>GIT_INDEX_FILE</c> redirects the index, and <c>GIT_OBJECT_DIRECTORY</c> with
    /// <c>GIT_ALTERNATE_OBJECT_DIRECTORIES</c> redirects the object database. Removing them loses nothing, because Git
    /// re-derives all of them from the working directory the request names. Variables that merely tune discovery (for
    /// example a ceiling directory) are deliberately not included: they cannot move the command into another repository.</remarks>
    internal static readonly string[] RepositoryRedirects = ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES"];

    /// <summary>Returns a copy of the request that removes every repository-redirecting variable in addition to the caller's removals.</summary>
    /// <param name="request">The request an adapter built; its existing removal list is preserved.</param>
    /// <returns>A copy whose removal list is the union of the caller's entries and <see cref="RepositoryRedirects"/>.</returns>
    /// <remarks>The removal list is applied by the process adapter after the request's environment additions, so a caller cannot
    /// reintroduce a redirect through <see cref="ProcessRequest.Environment"/>. Duplicates are dropped, which keeps the list valid
    /// when the same request is hardened twice. The original record is not mutated; a new one is returned.</remarks>
    internal static ProcessRequest ForRepository(ProcessRequest request) => request with
    {
        UnsetEnvironment = (request.UnsetEnvironment ?? []).Concat(RepositoryRedirects).Distinct().ToArray()
    };
}
