namespace Stacker.Core;

public sealed record GhStackRebasePlan(GhStackTarget Target, GhStackSnapshot Stack, IReadOnlyList<BranchRef> Refs,
    IReadOnlyList<BranchRef> UpperLayers);
