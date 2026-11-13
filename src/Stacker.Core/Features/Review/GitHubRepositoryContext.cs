using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Resolved GitHub identity for the repository origin and active gh account.</summary>
/// <remarks>
/// Every read and write is pinned to this context, so a settings change, a second account or a different clone cannot
/// silently retarget a queued operation. <see cref="Identity"/> is the scope used for draft keys, journals and object
/// references; <see cref="Display"/> is shown to the user and is not an identity.
/// </remarks>
/// <param name="Host">Lower-cased host the gh commands are pinned to, either github.com or a GitHub Enterprise host.</param>
/// <param name="RepositoryId">GitHub's numeric repository id. It is re-verified immediately before each publication because
/// owner and name can be renamed or re-created, while the number cannot be reassigned.</param>
/// <param name="Owner">Repository owner login exactly as GitHub reports it.</param>
/// <param name="Name">Repository name exactly as GitHub reports it.</param>
/// <param name="CloneUrl">Clone URL GitHub reports, used when pull request objects must be downloaded.</param>
/// <param name="User">Login of the active, valid gh account at connect time, so a later switch is detectable.</param>
/// <param name="DefaultBranch">The repository's default branch, used as the initial stack base and as a stack boundary.</param>
public sealed record GitHubRepositoryContext(string Host, long RepositoryId, string Owner, string Name,
    string CloneUrl, string User, string DefaultBranch)
{
    /// <summary>The stable scope key of this repository/account pair, shared by draft storage, journals and cached refs.</summary>
    /// <remarks>The numeric repository id stands in for owner/name so a rename keeps identity, and the login keeps one
    /// account from reading another account's review history on a shared machine.</remarks>
    public string Identity => $"{Host}/{RepositoryId}/{User}";
    /// <summary>Human-readable repository and account label for titles and headers.</summary>
    public string Display => $"{Host}/{Owner}/{Name} · @{User}";
}
