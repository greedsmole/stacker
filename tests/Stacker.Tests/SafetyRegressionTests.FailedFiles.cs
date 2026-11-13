using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;

namespace Stacker.Tests;

public sealed partial class SafetyRegressionTests
{
    internal sealed class FailedFiles : IAtomicFile
    {
        public Result<byte[]?> Read(string path) => Result<byte[]?>.Ok(null);
        public Task<Result<Unit>> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
            Task.FromResult(Result<Unit>.Fail(ProblemCodes.StoreWrite, "injected disk full"));
        public void SweepTemporaries(string path)
        {
        }

        Result<Unit> IAtomicFile.SweepTemporaries(string path) => Boundary.Capture(() => SweepTemporaries(path), ProblemCodes.StoreWrite);
    }
}
