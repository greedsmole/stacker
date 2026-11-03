using System.Text.Json.Serialization;
namespace Stacker.Core;

public sealed class WorkspaceState
{
    public int SchemaVersion { get; set; } = 1;
    public string? ActiveGroup { get; set; }
    public string[] StackKeys { get; set; } = [];
    public Dictionary<string, StackViewState> Stacks { get; set; } = [];
    public WorkspaceState Snapshot() => new()
    {
        SchemaVersion = SchemaVersion, ActiveGroup = ActiveGroup,
        Stacks = Stacks.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot())
    };
}
public sealed class StackViewState
{
    public DiffMode Mode { get; set; } = DiffMode.FullStack;
    public string[] Branches { get; set; } = [];
    public bool Expanded { get; set; } = true;
    public Dictionary<string, SectionViewState[]> Comparisons { get; set; } = [];
    [JsonIgnore] public long Revision { get; set; }
    public StackViewState Snapshot() => new()
    {
        Mode = Mode, Branches = Branches.ToArray(), Expanded = Expanded, Revision = Revision,
        Comparisons = new(Comparisons)
    };
}
public sealed record SectionViewState(string Layer, string? File, string Filter, double X, double Y, bool Expanded);
