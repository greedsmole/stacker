namespace Stacker.Core;

/// <summary>A status read bound to one checkout. HEAD can be absent in a new repository.</summary>
public sealed record WorkingTreeSnapshot(string Root, string? Head, WorkingTreeState State, string IndexRevision);
