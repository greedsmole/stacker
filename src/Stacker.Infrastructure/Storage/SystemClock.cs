using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure.Framework;

/// <summary>The clock commands and stores share.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}
