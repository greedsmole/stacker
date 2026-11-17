namespace Stacker.Core;

/// <summary>One branch that must be force-pushed after a successful restack, and what the plan believed the remote had.</summary>
/// <remarks>RemoteTip is captured before any branch moves and is used as the force-with-lease value; if origin moved after the preview the push fails instead of overwriting. A null value means the remote branch did not exist, and the lease then requires it to still be absent.</remarks>
/// <param name="Branch">Full local ref to publish.</param>
/// <param name="RemoteTip">Remote commit the lease requires, or null when the branch was absent.</param>
public sealed record PushStep(string Branch, string? RemoteTip);
