using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public sealed partial class FileTreeNode(string name, string path, FileTreeNode? parent, FileChange? change = null, object? item = null)
    : ObservableObject
{
    private readonly List<FileTreeNode>? _children = change is null ? [] : null;
    public string Name { get; } = name;
    public string Path { get; } = path;
    public FileChange? Change { get; } = change;
    internal object? Item { get; } = item;
    public DiffFileRow? ReviewRow => Item as DiffFileRow;
    public bool IsFolder => Change is null;
    public string AccessibleName => Change is { } file ? $"{file.DisplayPath} · {file.Kind}" : Name;
    public IReadOnlyList<FileTreeNode> Children => _children is { } children ? children : [];
    private FileTreeNode? Parent { get; } = parent;
    [ObservableProperty] private bool _isExpanded;

    internal void Add(FileTreeNode child) => _children!.Add(child);

    internal void Sort()
    {
        if (_children is not { } children)
            return;
        children.Sort((left, right) =>
        {
            var byKind = right.IsFolder.CompareTo(left.IsFolder);
            if (byKind != 0)
                return byKind;
            var byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(left.Name, right.Name);
        });
        foreach (var child in children)
            child.Sort();
    }

    internal void Reveal()
    {
        for (var folder = Parent; folder is not null; folder = folder.Parent)
            folder.IsExpanded = true;
    }
}
