using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed partial class ObjectStore
{
    private sealed class Entry
    {
        public int Leases; public bool Remove;
    }
}
