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
    public string? ActiveGroup
    {
        get; set;
    }
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
