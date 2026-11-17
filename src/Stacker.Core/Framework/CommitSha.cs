using System.Text.RegularExpressions;
namespace Stacker.Core.Framework;

/// <summary>A complete commit id; short ids and revisions are rejected.</summary>
/// <remarks>The pattern accepts exactly 40 lowercase hexadecimal characters (SHA-1) or 64 (SHA-256). Abbreviated ids
/// and revision expressions are refused on purpose: an abbreviation can become ambiguous as objects are added, and a
/// symbolic name such as a branch can resolve to a different commit later, so neither is a stable identity for a
/// journal entry. Uppercase is refused because Git reports lowercase ids and comparisons are ordinal. No abbreviation
/// is accepted or stored: the only shortening in this layer is the display-only seven-character label
/// (<see cref="SavedStash.Label"/>) and the removal of a remote name from
/// <see cref="RemoteTrackingRef.BranchName"/>; both are presentation, never identity. The pattern is anchored with
/// <c>\A</c> and <c>\z</c> rather than caret and dollar, so a trailing newline cannot satisfy it.</remarks>
public readonly partial record struct CommitSha
{
    /// <summary>The complete, lowercase commit id.</summary>
    public string Value
    {
        get;
    }
    /// <summary>Stores an already validated id; <see cref="Parse"/> is the public entry point.</summary>
    /// <param name="value">The validated commit id.</param>
    private CommitSha(string value) => Value = value;
    /// <summary>The generated whole-string pattern for a SHA-1 or SHA-256 id.</summary>
    /// <remarks>The alternation is wrapped in a non-capturing group so both alternatives are anchored. Keeping the
    /// pattern private means every caller goes through <see cref="IsValid"/> or <see cref="Parse"/> and cannot
    /// accidentally match a substring.</remarks>
    /// <returns>The compiled regular expression.</returns>
    [GeneratedRegex("\\A(?:[0-9a-f]{40}|[0-9a-f]{64})\\z")]
    private static partial Regex Pattern();
    /// <summary>Checks a complete commit id without creating a value; null is invalid.</summary>
    /// <param name="value">The candidate text.</param>
    /// <returns>True only for a complete 40- or 64-character lowercase hexadecimal id.</returns>
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);
    /// <summary>Creates a commit id, or reports that the text is not a complete id.</summary>
    /// <remarks>This is the guard used wherever an id comes from text (a stash reference, a commit id returned by an
    /// effect), so an abbreviation or a symbolic name is not stored as if it identified one immutable commit.</remarks>
    /// <param name="value">The candidate text.</param>
    /// <returns>The value, or <see cref="ProblemCodes.StateInvalid"/> with a display-ready message.</returns>
    public static Result<CommitSha> Parse(string? value) =>
        IsValid(value) ? Result<CommitSha>.Ok(new CommitSha(value!)) : Result<CommitSha>.Fail(ProblemCodes.StateInvalid, "Invalid commit id.");
    /// <summary>Returns the full commit id.</summary>
    /// <returns>The complete id.</returns>
    public override string ToString() => Value;
}
