namespace Stacker.Infrastructure;

/// <summary>Maps MSBuild's native paths to the exact spelling stored in the Git revision.</summary>
internal sealed class RoslynTrackedPaths(string root, IReadOnlySet<string> tracked)
{
    private readonly HashSet<string> _caseMatches = tracked.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);
    internal List<string> Diagnostics { get; } = [];

    internal string? Resolve(string file)
    {
        if (!Path.IsPathFullyQualified(file))
            return null;
        var relative = RoslynWorkspace.RelativePath(root, file);
        if (tracked.Contains(relative))
            return relative;
        if (_resolved.TryGetValue(relative, out var resolved))
            return resolved;
        if (!_caseMatches.Contains(relative))
            return null;

        resolved = File.Exists(file) ? NativeSpelling(relative) : null;
        if (resolved is null || !tracked.Contains(resolved))
        {
            Diagnostics.Add($"Loaded path cannot be mapped to a tracked file: {relative}.");
            resolved = null;
        }
        _resolved.Add(relative, resolved);
        return resolved;
    }

    private string? NativeSpelling(string relative)
    {
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            // Prefer an exact directory entry so distinct files on case-sensitive volumes stay distinct.
            var entries = Directory.EnumerateFileSystemEntries(current).ToArray();
            var entry = entries.FirstOrDefault(path => Path.GetFileName(path).Equals(segment, StringComparison.Ordinal));
            if (entry is null)
            {
                var matches = entries.Where(path => Path.GetFileName(path).Equals(segment, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (matches is not [var unique])
                    return null;
                entry = unique;
            }
            current = entry;
        }
        return RoslynWorkspace.RelativePath(root, current);
    }
}
