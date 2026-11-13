namespace Stacker.Core.Framework;

/// <summary>What went wrong at a boundary, with a stable code the UI can act on.</summary>
/// <remarks>The code comes from <see cref="ProblemCodes"/> and is stable enough for the UI, tests and diagnostics to
/// match; the message is ready to display, and the details carry raw tool output for the diagnostics tab.
/// <see cref="Certainty"/> records whether the failure may still have produced an effect. The record is immutable,
/// so the <c>Map</c> and <c>Bind</c> operators can pass the same instance through unchanged.</remarks>
/// <param name="Code">One of the catalogued codes; it names the failure class rather than a specific message.</param>
/// <param name="Message">Display-ready text that explains the failure to the user.</param>
/// <param name="Details">Raw supporting lines, such as tool output, kept for diagnostics; may be empty.</param>
public sealed record Problem(string Code, string Message, IReadOnlyList<string> Details)
{
    /// <summary>Whether the failed call may still have taken effect; defaults to unknown so an unclassified failure
    /// cannot be retried blindly.</summary>
    public EffectCertainty Certainty { get; init; } = EffectCertainty.Unknown;
    /// <summary>Creates a problem from a catalogued code and display text.</summary>
    /// <remarks>Prefer a <see cref="ProblemCodes"/> constant over a literal code, so the catalogue stays the single
    /// source of failure classes.</remarks>
    /// <param name="code">A catalogued code.</param>
    /// <param name="message">Display-ready text.</param>
    /// <param name="details">Optional raw supporting lines.</param>
    /// <returns>The problem, with certainty left at unknown.</returns>
    public static Problem Of(string code, string message, params string[] details) => new(code, message, details);
    /// <summary>Formats the code and message as one line for logs and exception text; the details are omitted
    /// because they belong in the diagnostics tab.</summary>
    /// <returns>The text <c>code: message</c>.</returns>
    public override string ToString() => Code + ": " + Message;
}
