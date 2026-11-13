using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Persisted per-repository GitHub settings that decide origin resolution and stack boundaries.</summary>
public sealed class RepositoryPreferences
{
    /// <summary>Explicit host/owner/repo used instead of the origin remote, for aliases or non-URL remotes.</summary>
    public string? GitHubOverride
    {
        get; set;
    }
    /// <summary>Base branch names that terminate discovery, so a pull request into the trunk is treated as a stack bottom.</summary>
    /// <remarks>Also used as the boundary set consulted while grouping; without it a chain would continue past the
    /// default branch or a release branch into unrelated pull requests.</remarks>
    public List<string>? BoundaryBranches
    {
        get; set;
    }
}
