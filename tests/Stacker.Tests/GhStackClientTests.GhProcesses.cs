using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Tests;

public sealed partial class GhStackClientTests
{
    private sealed class GhProcesses(string? release, string config, IProcessRunner git, string version = "0.1.0") : IProcessRunner
    {
        public Task<Result<ProcessResult>> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            if (request.Arguments.FirstOrDefault() != "stack")
                return git.RunAsync(request, ct);
            var arguments = request.Arguments.Skip(1).ToArray();
            if (release is null)
            {
                var stdout = arguments[0] == "--version" ? $"gh stack version {version}\n"
                    : """{"trunk":"main","currentBranch":"C","branches":[{"name":"A"},{"name":"B"},{"name":"C","needsRebase":true}]}""";
                Assert.True(arguments[0] is "--version" or "view");
                return Task.FromResult(Result<ProcessResult>.Ok(new(0, stdout, "")));
            }
            var environment = new Dictionary<string, string>(request.Environment ?? new Dictionary<string, string>())
            {
                ["GH_CONFIG_DIR"] = config,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = Stacker.Infrastructure.Framework.Neutral.EmptyConfigFile
            };
            return git.RunAsync(request with { Executable = release, Arguments = arguments, Environment = environment }, ct);
        }
    }
}
