using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

/// <summary>Builds folder nodes from Git paths and keeps the original selection objects at the leaves.</summary>
internal sealed class FileFolderTree
{
    private readonly FileTreeNode _root = new("", "", null);
    private readonly Dictionary<object, FileTreeNode> _items = [];
    public IReadOnlyList<FileTreeNode> Roots => _root.Children;
    public FileTreeNode? Find(object? item) => item is null ? null : _items.GetValueOrDefault(item);

    internal static FileFolderTree Create<T>(IEnumerable<T> items, Func<T, FileChange> fileFor, bool expandFolders = false,
        Func<T, (int Index, string Title)>? sectionFor = null) where T : class
    {
        var tree = new FileFolderTree();
        var sections = new Dictionary<int, FileTreeNode>();
        var folders = new Dictionary<(FileTreeNode Parent, string Name), FileTreeNode>();
        foreach (var item in items)
        {
            if (tree._items.ContainsKey(item))
                continue;
            var file = fileFor(item);
            var parent = tree._root;
            if (sectionFor is not null)
            {
                var (index, title) = sectionFor(item);
                if (!sections.TryGetValue(index, out var section))
                {
                    section = new(title, "", parent);
                    sections.Add(index, section);
                    parent.Add(section);
                }
                parent = section;
            }

            // Git paths always use '/'. A backslash is a valid character in a Git file name.
            var parts = (file.Kind == FileChangeKind.Deleted ? file.OldPath : file.NewPath).Split('/');
            for (var depth = 0; depth < parts.Length - 1; depth++)
            {
                var name = parts[depth];
                if (!folders.TryGetValue((parent, name), out var folder))
                {
                    var path = parent.Path.Length == 0 ? name : parent.Path + "/" + name;
                    folder = new(name, path, parent)
                    {
                        IsExpanded = expandFolders
                    };
                    folders.Add((parent, name), folder);
                    parent.Add(folder);
                }
                parent = folder;
            }
            var node = new FileTreeNode(parts[^1], file.DisplayPath, parent, file, item);
            parent.Add(node);
            tree._items.Add(item, node);
        }

        if (sectionFor is null)
            tree._root.Sort();
        else
            foreach (var section in tree.Roots)
                section.Sort();
        return tree;
    }
}
