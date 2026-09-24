using System.Text.Json;
using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed record DemoExpectation(string Stack, DiffMode Mode, int[] Positions, int Files, int Additions, int Deletions);
public sealed class DemoRepositoryGenerator(IProcessRunner runner, GitExecutable executable, IStackStore stacks, ApplicationStore store)
{
    public async Task<string> CreateAsync(CancellationToken ct = default)
    {
        var root = Path.Combine(store.Root, "demos", "demo-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        async Task<string> Git(params string[] args)
        {
            var result = await runner.RunAsync(new(executable.Resolve(), ["-c", "core.hooksPath=/dev/null", "-c", "commit.gpgSign=false", .. args], root,
                Environment: new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null", ["GIT_CONFIG_NOSYSTEM"] = "1" }), ct);
            if (result.ExitCode != 0) throw new StackerException("Demo: " + result.StdErr);
            return result.StdOut.Trim();
        }
        async Task Commit(string file, string text, string message) { await File.WriteAllTextAsync(Path.Combine(root, file), text, ct); await Git("add", "--", file); await Git("commit", "-m", message); }
        async Task Branch(string name, string from) => _ = await Git("checkout", "-b", name, from);
        await Git("init", "-b", "main"); await Git("config", "user.name", "Stacker demo"); await Git("config", "user.email", "demo@example.invalid"); await Git("config", "core.autocrlf", "false");
        await Commit("README.md", "# Stacker demo\n\nClick Authorization for all changes. Layer 2 shows only its contribution. Through this layer shows layers 1–2. Check 1 and 3 to compare those separately. Switch to Payments and back: the file/filter/scroll state is retained.\n\nGitHub data is offline. Publishing is disabled. Branches main, master and test are discovery boundaries.\n", "Demo instructions");
        await Branch("auth/model", "main");
        var auth = "namespace Demo;\npublic static class Auth\n{\n    public static string Login() => \"guest\";\n}\n";
        await Commit("Auth.cs", auth, "Session model");
        await Branch("auth/api", "auth/model"); auth = auth.Replace("    public static string", "    // Authenticate the current user.\n    public static string").Replace("guest", "user");
        await Commit("Auth.cs", auth, "Session API");
        await Branch("auth/ui", "auth/api"); auth = auth.Replace("\n}\n", "\n    public static bool IsEnabled => true;\n}\n"); await Commit("Auth.cs", auth, "Login screen");
        await Branch("payments/model", "main"); await Commit("Payment.cs", "namespace Demo;\npublic sealed record Payment(decimal Amount);\n", "Payment model");
        await Branch("payments/validation", "payments/model"); await Commit("Payment.cs", "namespace Demo;\n// Only positive amounts are accepted.\npublic sealed record Payment(decimal Amount);\n", "Payment validation");
        await Branch("shared/foundation", "main"); await Commit("Foundation.cs", "public sealed record Identity(string Value);\n", "Shared foundation");
        await Branch("shared/alpha", "shared/foundation"); await Commit("Alpha.cs", "public sealed record Alpha(Identity Id);\n", "Alpha feature");
        await Branch("shared/beta", "shared/foundation"); await Commit("Beta.cs", "public sealed record Beta(Identity Id);\n", "Beta feature");
        await Branch("master", "main"); await Branch("test", "master"); await Commit("environment.json", "{\"environment\":\"test\"}\n", "Test environment");
        await Branch("service/request", "test"); await Commit("Service.cs", "public sealed record ServiceRequest(string Name);\n", "Service request");
        await Git("checkout", "main");
        StackDefinition Stack(string id, string name, params string[] branches) => new(id, name, "refs/heads/main", branches.Select(b => "refs/heads/" + b).ToArray());
        await stacks.SaveAsync(root,
        [Stack("authorization", "Authorization", "auth/model", "auth/api", "auth/ui"), Stack("payments", "Payments", "payments/model", "payments/validation"),
         Stack("alpha", "Shared foundation · Alpha", "shared/foundation", "shared/alpha"), Stack("beta", "Shared foundation · Beta", "shared/foundation", "shared/beta"),
         Stack("broken", "Missing branch example", "branch/no-longer-exists")], null, ct);
        var prs = new List<PullRequest>();
        async Task Pr(long number, string title, string b, string h) => prs.Add(new(number, title, 1, b, await Git("rev-parse", b), 1, h, await Git("rev-parse", h), false, $"https://demo.local/demo/stacker/pull/{number}"));
        await Pr(101, "Session model", "main", "auth/model"); await Pr(102, "Session API", "auth/model", "auth/api"); await Pr(103, "Login screen", "auth/api", "auth/ui");
        await Pr(110, "Payment model", "main", "payments/model"); await Pr(111, "Payment validation", "payments/model", "payments/validation");
        await Pr(120, "Shared foundation", "main", "shared/foundation"); await Pr(121, "Alpha feature", "shared/foundation", "shared/alpha"); await Pr(122, "Beta feature", "shared/foundation", "shared/beta");
        await Pr(130, "Promote test environment", "master", "test"); await Pr(131, "Service request", "test", "service/request");
        var snapshot = new GitHubSnapshot(new("demo.local", 1, "demo", "stacker", "https://demo.local/demo/stacker.git", "demo", "main"), prs, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(root, ".stacker-demo.json"), JsonSerializer.Serialize(snapshot), ct);
        var expectations = new DemoExpectation[] { new("authorization", DiffMode.Layer, [1], 1, 2, 1), new("authorization", DiffMode.Cumulative, [1], 1, 6, 0), new("authorization", DiffMode.FullStack, [], 1, 7, 0), new("payments", DiffMode.FullStack, [], 1, 3, 0) };
        await File.WriteAllTextAsync(Path.Combine(root, "demo-manifest.json"), JsonSerializer.Serialize(expectations), ct);
        return root;
    }
    public static ReviewDiscussion Discussion(PullRequest pr) => new(
        [new("1", "reviewer", "Please check the contribution of this layer, then compare the entire stack.", pr.Url, DateTimeOffset.UtcNow)],
        [new("1", "reviewer", "The shape looks good; one line needs clarification.", "COMMENTED", DateTimeOffset.UtcNow)],
        [new("demo-current", false, false, false, new(pr.Number, pr.HeadSha, pr.BaseSha, "Auth.cs", "RIGHT", 4), [new("2", "reviewer", "Should authentication failure be represented explicitly?", pr.Url, DateTimeOffset.UtcNow)]),
         new("demo-file", false, false, false, null, [new("4", "reviewer", "Could this file also document the session expiry policy?", pr.Url, DateTimeOffset.UtcNow)], "Auth.cs", true),
         new("demo-outdated", false, true, false, null, [new("3", "reviewer", "This comment refers to an older version of the file.", pr.Url, DateTimeOffset.UtcNow)], "Auth.cs")]);
}
