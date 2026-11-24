namespace Stacker.Core;

public sealed record GhStackBranch(string Name, bool IsMerged, bool IsQueued, bool NeedsRebase);
