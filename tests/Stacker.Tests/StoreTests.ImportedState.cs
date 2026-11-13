using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;

public sealed partial class StoreTests
{
    private sealed class ImportedState
    {
        public string? ActiveGroup
        {
            get; set;
        }
    }
}
