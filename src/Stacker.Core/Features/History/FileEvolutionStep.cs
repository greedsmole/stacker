using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One layer's contribution to a file's evolution, describing what the layer did to the tracked path.</summary>
/// <remarks>Steps are held oldest first; the last step is the newest state and carries the path the caller selected. A
/// step without a file entry still appears, so a gap in the walk stays visible instead of silently shortening the history.</remarks>
/// <param name="Change">Change that owns the layer and supplies the title used in attributions.</param>
/// <param name="Version">Captured version whose merge-base-to-head diff was inspected.</param>
/// <param name="File">File entry for the tracked path in that diff, or <see langword="null"/> when the version has no merge
/// base or its diff holds zero or several entries for the path.</param>
/// <param name="Continuous">True when the older neighbouring layer's head is this version's merge base — or when this is the
/// oldest step, which has nothing older to compare — proving the layer was captured directly on top of the previous one.
/// False marks an ancestry gap across which line numbers cannot be translated.</param>
public sealed record FileEvolutionStep(ChangeNode Change, ChangeVersion Version, FileChange? File, bool Continuous);
