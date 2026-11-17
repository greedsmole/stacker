using System.Text.RegularExpressions;
namespace Stacker.Core.Framework;

/// <summary>A stash commit recorded so the saved changes can be restored later.</summary>
/// <remarks>The stash is identified by its full commit id rather than by <c>stash@{n}</c>, because the index shifts
/// whenever another stash is added or dropped; an index recorded before a restart could restore the wrong changes.
/// Restore finds this id in the stash list and pops the matching index, so newer stashes are left alone.</remarks>
public readonly record struct StashRef
{
    /// <summary>The commit id of the stash commit.</summary>
    public CommitSha Commit
    {
        get;
    }
    /// <summary>Stores a validated stash commit; <see cref="Parse"/> is the public entry point.</summary>
    /// <param name="commit">The validated stash commit.</param>
    private StashRef(CommitSha commit) => Commit = commit;
    /// <summary>Validates a stash commit id.</summary>
    /// <remarks>Uses the same complete-id rule as <see cref="CommitSha"/>, so an abbreviation or symbolic stash
    /// name fails with <see cref="ProblemCodes.StateInvalid"/>; the message is the generic invalid-commit-id text
    /// rather than a stash-specific one.</remarks>
    /// <param name="value">The candidate stash commit id.</param>
    /// <returns>The stash reference, or the commit-id problem.</returns>
    public static Result<StashRef> Parse(string? value) => CommitSha.Parse(value).Map(commit => new StashRef(commit));
    /// <summary>Returns the full stash commit id.</summary>
    /// <returns>The commit id.</returns>
    public override string ToString() => Commit.Value;
}
