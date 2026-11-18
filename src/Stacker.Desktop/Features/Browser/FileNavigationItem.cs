using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

internal sealed record FileNavigationItem(FileChange Change, object Target, int SectionIndex = 0, string SectionTitle = "")
{
    private string Path => Change.Kind == FileChangeKind.Deleted ? Change.OldPath : Change.NewPath;
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    public string Directory => Path[..(Path.LastIndexOf('/') + 1)].TrimEnd('/');
    public bool HasDirectory => Directory.Length > 0;
    public string DisplayPath => Change.DisplayPath;
    public string AccessibleName => $"{DisplayPath} · {Change.Kind}";
    public DiffFileRow? ReviewRow => Target as DiffFileRow;
}
