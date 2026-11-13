using System.Text.Json;
using System.Text.RegularExpressions;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

internal static class JsonHelpers
{
    internal static bool TryGetInt32Safe(this JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }
}
