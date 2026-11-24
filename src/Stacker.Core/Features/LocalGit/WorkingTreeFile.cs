namespace Stacker.Core;

/// <summary>One side of a file in the index or working tree.</summary>
public sealed record WorkingTreeFile(WorkingTreeEntry Entry, WorkingTreeSide Side)
{
    public char Status => Side == WorkingTreeSide.Staged ? Entry.Status[0] : Entry.Status[1];
    public string Path => Entry.Path;
    public string OldPath => Status is 'R' or 'C' ? Entry.OriginalPath ?? Path : Path;
    public string DisplayPath => OldPath == Path ? Path : $"{OldPath} → {Path}";
    public string AccessibleName => $"{Status} {DisplayPath}";
    public bool IsUntracked => Entry.IsUntracked;
    public bool IsAvailable => Side == WorkingTreeSide.Staged ? Entry.IsStaged : Entry.IsUnstaged;
}
