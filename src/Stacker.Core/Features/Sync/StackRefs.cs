using Stacker.Core.Framework;

namespace Stacker.Core;

/// <summary>Short display name for a full Git ref.</summary>
/// <remarks>A remote-tracking ref keeps its remote name. The result is for labels and messages, not for an update-ref argument.</remarks>
public static class StackRefs
{
    /// <summary>Strips the local or remote-tracking prefix for display.</summary>
    /// <param name="reference">Full ref, or a name without a prefix.</param>
    /// <returns>The shortened name; a name without either prefix is returned unchanged.</returns>
    public static string Short(string reference) =>
        reference.StartsWith(LocalBranch.Prefix, StringComparison.Ordinal) ? reference[LocalBranch.Prefix.Length..]
        : reference.StartsWith(RemoteTrackingRef.Prefix, StringComparison.Ordinal) ? reference[RemoteTrackingRef.Prefix.Length..]
        : reference;
}
