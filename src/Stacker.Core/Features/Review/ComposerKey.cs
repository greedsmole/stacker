using System.Text.Json;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Owns a draft destination while preserving the existing persisted encoding.</summary>
public sealed record ComposerKey
{
    public string Encoded { get; }
    private ComposerKey(string encoded) => Encoded = encoded;
    public static ComposerKey Pr { get; } = new("pr");
    public static Result<ComposerKey> Parse(string value)
    {
        if (value is "pr" or "review") return Result<ComposerKey>.Ok(new(value));
        if ((value.StartsWith("reply:", StringComparison.Ordinal) && value.Length > 6)
            || (value.StartsWith("file:", StringComparison.Ordinal) && value.Length > 5)) return Result<ComposerKey>.Ok(new(value));
        if (value.StartsWith("line:", StringComparison.Ordinal))
        {
            try
            {
                var anchor = JsonSerializer.Deserialize<ReviewAnchor>(value[5..]);
                if (anchor is { PullRequest: > 0, Line: > 0, Side: "LEFT" or "RIGHT" } && !string.IsNullOrWhiteSpace(anchor.Path))
                    return Result<ComposerKey>.Ok(new(value));
            }
            catch (JsonException) { }
        }
        return Result<ComposerKey>.Fail(ProblemCodes.StateInvalid, "The draft destination is invalid.");
    }
}
