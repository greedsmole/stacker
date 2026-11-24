namespace Stacker.Tests;

public sealed partial class GitFixture
{
    // Capture before the seed is deleted; no shared checkout, hardlinks or temporary seed directories survive.
    private sealed class RepositorySnapshot(IReadOnlyDictionary<string, byte[]> files)
    {
        public static RepositorySnapshot Capture(string root) => new(
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                // Sample hooks are never executed and make every fixture needlessly copy another directory.
                .Where(path => !Path.GetRelativePath(root, path).StartsWith(Path.Combine(".git", "hooks") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes));

        public void Restore(string root)
        {
            foreach (var (relative, bytes) in files)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
        }
    }
}
