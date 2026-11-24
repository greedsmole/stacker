namespace Stacker.Core;

public sealed record GhStackSnapshot(string Trunk, string CurrentBranch, IReadOnlyList<GhStackBranch> Branches);
