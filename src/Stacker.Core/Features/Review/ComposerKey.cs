using System.Text.Json;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Names one draft destination so the conversation, review, file, reply and line composers keep separate unsent text.</summary>
/// <remarks>
/// The three review tabs used to share one buffer, so switching tab or file overwrote text that had not been sent yet
/// (m20 in <c>docs/MIGRATION.md</c>). The encoded string is the dictionary key persisted in
/// <see cref="ReviewDraft.ContextComposers"/>, so equality is equality of that text and a destination survives
/// navigation, reload and restart without a second identity type. A key is never minted from arbitrary text: only the
/// shapes accepted by <see cref="Parse"/> are addressable, because a typo would otherwise open an empty buffer instead
/// of reporting a damaged draft.
/// </remarks>
public sealed record ComposerKey
{
    /// <summary>The persisted key text, used for equality and for the buffer dictionary lookups.</summary>
    public string Encoded { get; }
    /// <summary>Stores an already validated key; validation lives in <see cref="Parse"/> so no other code can mint one.</summary>
    /// <param name="encoded">One of the encoded shapes accepted by <see cref="Parse"/>.</param>
    private ComposerKey(string encoded) => Encoded = encoded;
    /// <summary>The pull-request conversation destination, which is the default buffer and the one mirrored by <see cref="ReviewDraft.Composer"/>.</summary>
    public static ComposerKey Pr { get; } = new("pr");
    /// <summary>Decodes a persisted destination key, refusing anything that is not one of the supported shapes.</summary>
    /// <remarks>
    /// Accepted shapes:
    /// <list type="bullet">
    /// <item><description><c>pr</c> — the pull-request conversation composer.</description></item>
    /// <item><description><c>review</c> — accepted for drafts that already contain it; no current view selects this shape.</description></item>
    /// <item><description><c>reply:</c> plus a thread id — one buffer per review thread, so two threads never share a reply draft.</description></item>
    /// <item><description><c>file:</c> plus a path — accepted although the current file panel keys its own buffer map by plain path.</description></item>
    /// <item><description><c>line:</c> plus the JSON form of a <see cref="ReviewAnchor"/> — one buffer per diff selection.</description></item>
    /// </list>
    /// The JSON is serialized with the default options from the selection code, so hand-edited camelCase JSON does not
    /// bind. The <c>reply:</c> and <c>file:</c> length checks compare against the prefix length (six and five characters),
    /// so they only require at least one character of id or path after the colon. For <c>line:</c> the five-character
    /// prefix is stripped before JSON binding, and only the address is checked: positive pull request and line, side
    /// <c>LEFT</c> or <c>RIGHT</c>, and a non-blank path. The anchor's head and base SHAs are deliberately not compared
    /// with the current pull request here — a selection from an older version must still resolve to a buffer so its text can
    /// be recovered or reanchored, while version agreement is enforced at publication time. A malformed JSON key is
    /// swallowed and reported as a problem instead of throwing during draft load.
    /// </remarks>
    /// <param name="value">Persisted key text to decode.</param>
    /// <returns>The destination for a supported key; otherwise a refused <see cref="Result{T}"/> with
    /// <see cref="ProblemCodes.StateInvalid"/>, which the review view unwraps so the user sees the problem rather than an
    /// empty composer.</returns>
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
