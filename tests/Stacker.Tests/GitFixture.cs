using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class GitFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Stacker tests ü " + Guid.NewGuid().ToString("N"));
    public ProcessRunner Runner { get; } = new();
    public GitRepositoryReader Reader { get; }
    private readonly List<string> _worktrees = [];
    public GitFixture() { Directory.CreateDirectory(Root); Reader = new(Runner, new()); }
    public async Task<string> Git(params string[] args)
    {
        var result = await Runner.RunAsync(new(new GitExecutable().Resolve(), args, Root, Environment: new Dictionary<string, string> { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null", ["GIT_TERMINAL_PROMPT"] = "0" }));
        Assert.True(result.ExitCode == 0, result.StdErr);
        return result.StdOut.TrimEnd('\r', '\n');
    }
    public async Task Init()
    {
        await Git("init", "-b", "main"); await Git("config", "user.email", "test@example.invalid"); await Git("config", "user.name", "Stacker tests");
        await Git("config", "core.autocrlf", "false");
        await Write("base.txt", "base\n"); await Commit("base");
    }
    public async Task Write(string path, string text) { var full = Path.Combine(Root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); await File.WriteAllTextAsync(full, text); }
    public async Task Commit(string message) { await Git("add", "--all"); await Git("commit", "-m", message); }
    public async Task Linear()
    {
        await Init();
        foreach (var branch in new[] { "A", "B", "C" }) { await Git("checkout", "-b", branch); await Write(branch + ".txt", branch + "\n"); await Commit(branch); }
    }
    public StackDefinition Stack => new("test", "Test", "refs/heads/main", ["refs/heads/A", "refs/heads/B", "refs/heads/C"]);
    public async Task<string> AddWorktree()
    {
        var path = Root + " worktree"; await Git("worktree", "add", "--detach", path); _worktrees.Add(path); return path;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var path in _worktrees) { await Git("worktree", "remove", "--force", path); }
        // Git object files can be read-only on Windows.
        foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(Root, true);
    }
}
