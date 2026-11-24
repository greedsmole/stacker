using System.Diagnostics;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

public sealed partial class GhStackClient
{
    private async Task<ProcessResult> Run(GhStackTarget target, string[] arguments, TimeSpan timeout, CancellationToken ct)
    {
        string[] full = ["stack", .. arguments];
        var start = Stopwatch.StartNew();
        var entry = log?.BeginGitHub(full, DateTimeOffset.UtcNow);
        ProcessResult? result = null;
        try
        {
            var environment = ChildEnvironment(target);
            var unset = gh.UnsetEnvironment.Concat(["GH_FORCE_TTY"]).ToArray();
            result = (await processes.RunAsync(new(gh.Resolve(), full, target.Root, timeout, MaxOutputBytes: 1024 * 1024,
                Environment: environment, UnsetEnvironment: unset), ct)).OrThrow();
            return result;
        }
        finally
        {
            log?.Finish(entry, start.Elapsed, result?.ExitCode, result?.StdErr ?? "GitHub CLI did not finish.", result?.ExitCode != 0);
        }
    }

    private Dictionary<string, string> ChildEnvironment(GhStackTarget target)
    {
        var paths = new[] { gitExecutable.Resolve(), gh.Resolve() }.Where(Path.IsPathFullyQualified)
            .Select(Path.GetDirectoryName).OfType<string>();
        return new(GitRunner.WriteEnvironment)
        {
            ["PATH"] = string.Join(Path.PathSeparator, paths.Append(Environment.GetEnvironmentVariable("PATH") ?? "")),
            ["GH_REPO"] = target.Repository ?? "",
            ["GH_PROMPT_DISABLED"] = "1",
            ["GH_PAGER"] = "cat",
            ["GH_DEBUG"] = "",
            ["GH_STACK_NO_UPDATE_NOTIFIER"] = "1",
            ["NO_COLOR"] = "1"
        };
    }
}
