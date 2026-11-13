using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class LocalGitTests
{
    /// <summary>Records launches so an editor can be checked without opening one.</summary>
    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<ProcessRequest> Started { get; } = [];
        public void Start(ProcessRequest request) => Started.Add(request);

        Result<Unit> IProcessLauncher.Start(ProcessRequest request) => Boundary.Capture(() => Start(request), ProblemCodes.GitFailed);
    }
}
