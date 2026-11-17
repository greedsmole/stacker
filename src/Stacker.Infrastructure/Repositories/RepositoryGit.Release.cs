using System.Collections.Concurrent;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
namespace Stacker.Infrastructure;

public sealed partial class RepositoryGit
{
    private sealed class Release(Action action) : IDisposable
    {
        private Action? _action = action; public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
