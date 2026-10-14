// Purpose: Builds and cleans up isolated temporary repositories/worktrees so Git integration scenarios never use the user checkout.
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;
public sealed class GitFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Stacker tests ü " + Guid.NewGuid().ToString("N"));
    public ProcessRunner Runner { get; } = new();
    public GitRepositoryReader Reader { get; }
    /// <summary>Local bare repository used as origin so push tests never touch the network.</summary>
    public string? RemoteRoot { get; private set; }
    private readonly List<string> _worktrees = [];
    private static readonly Dictionary<string, string> IsolatedEnvironment = new()
    { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = Stacker.Infrastructure.Framework.Neutral.EmptyConfigFile, ["GIT_TERMINAL_PROMPT"] = "0" };
    public GitFixture() { Directory.CreateDirectory(Root); Reader = new(Runner, new()); }
    public async Task<string> Git(params string[] args)
    {
        var result = await Runner.RunAsync(new(new GitExecutable().Resolve(), args, Root, Environment: IsolatedEnvironment));
        Assert.True(result.ExitCode == 0, result.StdErr);
        return result.StdOut.TrimEnd('\r', '\n');
    }
    /// <summary>A journal for this fixture, stored outside the repository like the application does.</summary>
    public Stacker.Core.Framework.IJournal CreateJournal() =>
        new JsonJournal(new JsonStateStore<JournalLog>(new AtomicFile(), Root + " journal"), new SystemClock());
    /// <summary>A write-path adapter wired to this fixture's journal.</summary>
    public GitBranchWriter Writer() => new(Runner, new GitExecutable(), Reader, CreateJournal(), new SystemClock());
    /// <summary>The repository path Git itself reports, which resolves temporary-directory symlinks.</summary>
    public async Task<string> ResolvedRoot() => await Git("rev-parse", "--show-toplevel");
    /// <summary>Runs Git that is expected to fail, returning its exit code instead of asserting.</summary>
    public async Task<int> ExitCode(params string[] args) => (await Runner.RunAsync(new(new GitExecutable().Resolve(), args, Root, Environment: IsolatedEnvironment))).ExitCode;
    /// <summary>Runs Git outside the fixture checkout, for the bare remote a push test owns.</summary>
    public async Task<string> GitAt(string directory, params string[] args)
    {
        var result = await Runner.RunAsync(new(new GitExecutable().Resolve(), args, directory, Environment: IsolatedEnvironment));
        Assert.True(result.ExitCode == 0, result.StdErr);
        return result.StdOut.TrimEnd('\r', '\n');
    }
    /// <summary>Adds an empty bare repository called origin without fetching or cloning anything.</summary>
    public async Task AddOrigin()
    {
        RemoteRoot = Root + " remote.git";
        var result = await Runner.RunAsync(new(new GitExecutable().Resolve(), ["init", "--bare", "--initial-branch=main", RemoteRoot], Root, Environment: IsolatedEnvironment));
        Assert.True(result.ExitCode == 0, result.StdErr);
        await Git("remote", "add", "origin", RemoteRoot);
    }
    /// <summary>Reads a ref from the bare remote, standing in for another developer's push.</summary>
    public Task<string> RemoteSha(string reference) => GitAt(RemoteRoot!, "rev-parse", reference);
    /// <summary>Moves a ref in the bare remote without touching this checkout.</summary>
    public Task MoveRemote(string reference, string sha) => GitAt(RemoteRoot!, "update-ref", reference, sha);
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
        // Cancelled ViewModel loads release their process working directories asynchronously.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(Root, true); break; }
            catch (IOException) when (OperatingSystem.IsWindows() && attempt < 20) { await Task.Delay(100); }
        }
        if (RemoteRoot is not null && Directory.Exists(RemoteRoot)) Directory.Delete(RemoteRoot, true);
    }
}
