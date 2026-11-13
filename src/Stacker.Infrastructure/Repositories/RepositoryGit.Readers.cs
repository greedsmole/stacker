using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed partial class RepositoryGit
{
    private sealed class Readers
    {
        public int Count;
        public Dictionary<string, RepositoryIdentity> Pending { get; } = [];
    }
}
