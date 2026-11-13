using System.Text.Json.Serialization;
namespace Stacker.Core;

/// <summary>The persisted view of one stack group: the mode and selection it was shown in, its expanded state and its
/// cached comparison views.</summary>
/// <remarks>One instance exists per stack key and lives either inside <see cref="WorkspaceState.Stacks"/> (layout 1) or
/// in its own document (layout 2). It holds presentation state only: no diff content and no commit ids are persisted
/// here, so a stored view can never be mistaken for a comparison result.</remarks>
public sealed class StackViewState
{
    /// <summary>The comparison mode last used for this stack.</summary>
    /// <remarks>Restored before the first comparison, so reopening a stack shows the mode it was left in. A load
    /// replaces an undefined value with <see cref="DiffMode.FullStack"/>, because JSON can carry an integer that no
    /// longer maps to a member and a mode switch would otherwise have no branch to take.</remarks>
    public DiffMode Mode { get; set; } = DiffMode.FullStack;
    /// <summary>Full refs of the layers selected for the last displayed view; empty when no layer was selected.</summary>
    /// <remarks>Refs are stored instead of layer positions so a selection survives an edit elsewhere in the stack; a ref
    /// that no longer exists matches no section when the view is restored. For a discovered PR stack these are the
    /// synthetic layer refs built from the pull request numbers.</remarks>
    public string[] Branches { get; set; } = [];
    /// <summary>Whether the stack node was expanded in the navigation tree.</summary>
    /// <remarks>Defaults to <see langword="true"/>, so a stack captured for the first time is expanded. The workspace
    /// controller records it only when it differs from the group's current state, so toggling it is one of the changes
    /// that bump <see cref="Revision"/> and make a save write this document.</remarks>
    public bool Expanded { get; set; } = true;
    /// <summary>Per-section presentation state, keyed by the comparison view it belongs to.</summary>
    /// <remarks>The key combines the mode and the selected layer refs, for example <c>FullStack:</c> when the whole stack
    /// is shown, so each combination remembers its own selected file, filter, scroll position and expanded sections.
    /// The cache is bounded to 40 views per stack: a load keeps the last 40 entries and the workspace controller trims
    /// older entries while capturing so the view on screen always survives. Each value holds one section per layer and
    /// no diff content.</remarks>
    public Dictionary<string, SectionViewState[]> Comparisons { get; set; } = [];
    /// <summary>Marker used to detect an unchanged view; never serialized.</summary>
    /// <remarks><c>JsonIgnore</c> keeps it out of the document, so a loaded view starts at 0 and matches the revision
    /// that <see cref="WorkspacePersistence.LoadAsync"/> seeds for a document it has just read. The UI increments it for
    /// every captured change, and a save skips a stack whose marker still equals the revision already on disk.</remarks>
    [JsonIgnore]
    public long Revision
    {
        get; set;
    }
    /// <summary>Copies this view so a queued save is isolated from later changes.</summary>
    /// <returns>A new view with the same mode, expanded state and revision and its own copies of the branch array and the
    /// comparison dictionary.</returns>
    /// <remarks>The section arrays inside the dictionary are shared rather than cloned, because a capture replaces them
    /// wholesale; only the containers that would otherwise be mutated during a save need new instances.</remarks>
    public StackViewState Snapshot() => new()
    {
        Mode = Mode,
        Branches = Branches.ToArray(),
        Expanded = Expanded,
        Revision = Revision,
        Comparisons = new(Comparisons)
    };
}
