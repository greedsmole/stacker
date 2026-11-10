using System.Text.Json.Serialization;
namespace Stacker.Core;

/// <summary>The persisted workspace document: which stack view is active and, in the older layout, every stack view.</summary>
/// <remarks>Serialized as JSON under the <c>workspace</c> kind. <see cref="SchemaVersion"/> 1 is the historical document
/// that carries <see cref="Stacks"/> inline; 2 is the index that carries only <see cref="ActiveGroup"/> and
/// <see cref="StackKeys"/>, with each stack in its own document. Saving always writes layout 2, and a document claiming
/// a newer version is refused by <see cref="WorkspacePersistence.LoadAsync"/> instead of being overwritten, because a
/// downgrade would silently drop the fields it does not know.</remarks>
public sealed class WorkspaceState
{
    /// <summary>Layout of this document: 1 = stack views inline, 2 = index over one document per stack.</summary>
    /// <remarks>Defaults to 1, so a document that omits the field is read as the historical layout whose stacks are
    /// inline. <see cref="WorkspacePersistence.SaveAsync"/> always writes the value 2.</remarks>
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Group key of the stack that was active when the workspace was last captured.</summary>
    /// <remarks>Restored so reopening a repository returns to the stack the user was comparing;
    /// <see langword="null"/> means no stack had been selected yet. The form is <c>local:&lt;stack id&gt;</c> for an
    /// authored stack or <c>github:&lt;context identity&gt;:&lt;stack id&gt;</c> for a discovered PR stack, which is
    /// also the key space of <see cref="Stacks"/>.</remarks>
    public string? ActiveGroup { get; set; }
    /// <summary>Stack keys whose documents exist; written by the layout-2 index and unused by layout 1.</summary>
    /// <remarks>A load uses it as the list of per-stack documents to read, and the duplicate removal there keeps a
    /// hand-edited index from reading the same document twice. It is empty in a layout-1 document, whose stacks are
    /// inline. Dropping a key stops that document being read; it does not remove it from storage.</remarks>
    public string[] StackKeys { get; set; } = [];
    /// <summary>Views by stack key: inline content in layout 1, content read from per-stack documents in layout 2.</summary>
    /// <remarks>A layout-2 index is saved with this empty, so the dictionary here describes the loaded views rather than
    /// a field that the index persists. Keying by group lets an authored stack and a discovered PR stack remember their
    /// own mode, selection and cached comparisons independently.</remarks>
    public Dictionary<string, StackViewState> Stacks { get; set; } = [];
    /// <summary>Deep copy used to queue a save without letting later changes reach the queued document.</summary>
    /// <returns>A new state with the same schema version and active group and a cloned view per stack.</returns>
    /// <remarks><see cref="StackKeys"/> is deliberately not copied, because the saver derives the index from the stack
    /// keys it actually holds. The copy is one level deep per stack, which is sufficient because a capture replaces each
    /// comparison array wholesale instead of mutating it in place.</remarks>
    public WorkspaceState Snapshot() => new()
    {
        SchemaVersion = SchemaVersion,
        ActiveGroup = ActiveGroup,
        Stacks = Stacks.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot())
    };
}
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
    [JsonIgnore] public long Revision { get; set; }
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
/// <summary>Presentation state of one comparison section, restored when the same view is shown again.</summary>
/// <param name="Layer">The layer the section shows, as the full ref reported in the comparison result; it is the key
/// that re-associates a stored section with a freshly compared one.</param>
/// <param name="File">Repository-relative path of the selected file, or <see langword="null"/> when the section had no
/// file selected.</param>
/// <param name="Filter">The file filter text that was typed in this section.</param>
/// <param name="X">Horizontal scroll offset of the diff list, in the control's device-independent units.</param>
/// <param name="Y">Vertical scroll offset of the diff list, in the control's device-independent units.</param>
/// <param name="Expanded">Whether the section was expanded.</param>
public sealed record SectionViewState(string Layer, string? File, string Filter, double X, double Y, bool Expanded);
