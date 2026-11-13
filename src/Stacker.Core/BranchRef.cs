namespace Stacker.Core;

/// <summary>A named ref resolved to its immutable commit ID in one repository snapshot.</summary>
/// <param name="Name">Full ref name, for example <c>refs/heads/main</c>.</param>
/// <param name="Sha">Commit ID the ref pointed at when the snapshot was taken.</param>
public sealed record BranchRef(string Name, string Sha)
{
    /// <summary>The ref with its <c>refs/heads/</c> or <c>refs/remotes/</c> prefix removed, for display.</summary>
    /// <remarks>Only those two branch prefixes are stripped, so any other ref keeps its full namespace. This is display text only:
    /// comparisons and lookups always use <c>Name</c>, which is why stripping cannot change which object a comparison reads.</remarks>
    public string DisplayName => Name.Replace("refs/heads/", "").Replace("refs/remotes/", "");
    /// <summary>Returns <see cref="DisplayName"/>, so a bound or interpolated ref reads as a branch name instead of a full ref.</summary>
    public override string ToString() => DisplayName;
}
