using System.Text.RegularExpressions;
namespace Stacker.Core.Framework;

/// <summary>A remote-tracking reference, which can be compared against but never rewritten.</summary>
/// <remarks>The value only has to carry the <see cref="Prefix"/> namespace and a non-empty short name. The stricter
/// character guard used for local branches is not repeated because this value is never passed back as a rewrite
/// target; it is read from Git's ref listing and used for comparison and naming.</remarks>
public readonly record struct RemoteTrackingRef
{
    /// <summary>The exact namespace for remote-tracking refs; comparison is ordinal.</summary>
    public const string Prefix = "refs/remotes/";
    /// <summary>The full reference, used when reading the tracked commit.</summary>
    public string Ref
    {
        get;
    }
    /// <summary>Everything after the prefix: the remote name plus the branch name.</summary>
    public string ShortName
    {
        get;
    }
    /// <summary>Splits the validated reference; the constructor assumes the prefix is already present.</summary>
    /// <param name="reference">A validated remote-tracking reference.</param>
    private RemoteTrackingRef(string reference)
    {
        Ref = reference;
        ShortName = reference[Prefix.Length..];
    }
    /// <summary>Validates that the reference is a remote-tracking ref.</summary>
    /// <param name="reference">The candidate full reference.</param>
    /// <returns>The value, or <see cref="ProblemCodes.StateInvalid"/> when the namespace or name is wrong.</returns>
    public static Result<RemoteTrackingRef> Parse(string? reference) =>
        reference is not null && reference.StartsWith(Prefix, StringComparison.Ordinal) && reference.Length > Prefix.Length
            ? Result<RemoteTrackingRef>.Ok(new RemoteTrackingRef(reference))
            : Result<RemoteTrackingRef>.Fail(ProblemCodes.StateInvalid, "Not a remote-tracking reference.");
    /// <summary>The branch name a GitHub pull request would use for this ref ("origin/main" becomes "main").</summary>
    /// <remarks>Only the first segment, the remote name, is dropped; everything after the first slash is kept, so a
    /// branch that itself contains slashes survives. A short name without a slash is returned unchanged.</remarks>
    public string BranchName => ShortName.Contains('/') ? ShortName[(ShortName.IndexOf('/') + 1)..] : ShortName;
    /// <summary>Returns the short form, including the remote name.</summary>
    /// <returns>The reference without its namespace.</returns>
    public override string ToString() => ShortName;
}
