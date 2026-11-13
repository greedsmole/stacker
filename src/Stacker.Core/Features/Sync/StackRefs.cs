namespace Stacker.Core;

/// <summary>Short display name for a full Git ref.</summary>
/// <remarks>Both prefixes are removed with replacement rather than a prefix check, so a remote-tracking ref keeps its remote name ("refs/remotes/origin/A" becomes "origin/A"). The result is for labels and messages, not for an update-ref argument.</remarks>
public static class StackRefs
{
    /// <summary>Strips the local or remote-tracking prefix for display.</summary>
    /// <param name="reference">Full ref, or a name without a prefix.</param>
    /// <returns>The shortened name; a name without either prefix is returned unchanged.</returns>
    public static string Short(string reference) => reference.Replace("refs/heads/", "").Replace("refs/remotes/", "");
}
