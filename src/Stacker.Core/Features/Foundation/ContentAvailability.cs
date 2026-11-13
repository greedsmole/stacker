using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Whether the historical commits of one scope can be read from the opened repository right now.</summary>
/// <remarks>Stored as its integer value in the history database, and the "explicitly removed" lookup compares against
/// the literal 2, so the order of these members is a persistence contract and must not be rearranged.</remarks>
public enum ContentAvailability
{
    /// <summary>Every pinned commit of the scope is present in the opened repository, so history can be read offline.</summary>
    Available,
    /// <summary>No history pins exist for the scope yet; opening or loading the layer is what pins them.</summary>
    NeedsDownload,
    /// <summary>The user explicitly released the history pins for this scope.</summary>
    /// <remarks>Metadata, checkpoints and drafts remain, and a later import does not restore the scope silently.
    /// Callers surface this as an explanation rather than as an empty history.</remarks>
    Removed,
    /// <summary>Pins or objects that were recorded are no longer available in this repository, so history cannot be read.</summary>
    /// <remarks>Also reported when a scope was previously marked available but its pins have disappeared, which keeps
    /// "nothing downloaded yet" distinguishable from "the objects are gone".</remarks>
    Unavailable
}
