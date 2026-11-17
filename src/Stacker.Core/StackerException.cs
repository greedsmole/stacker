namespace Stacker.Core;

/// <summary>Expected user-facing failure that can be shown without treating it as an application crash.</summary>
/// <remarks>Fallible ports return failed <see cref="Framework.Result{T}"/> values instead of throwing across the boundary; the UI and the Core
/// services above them unwrap those results, and that rethrow becomes this type, keeping the catalogue code in <see cref="Framework.Problem"/>. A
/// message-only instance carries no code and no certainty, so callers cannot switch on it - only the text is available.</remarks>
public class StackerException : Exception
{
    /// <summary>The catalogued problem behind the failure, or null when only a message was supplied.</summary>
    /// <remarks>Carries the code the UI switches on and, through <see cref="Framework.EffectCertainty"/>, whether the change already happened,
    /// which decides whether recovery may offer an undo or must ask the user to inspect.</remarks>
    public Framework.Problem? Problem
    {
        get;
    }
    /// <summary>Creates a failure with display text but no catalogue code.</summary>
    /// <param name="message">Text shown to the user.</param>
    public StackerException(string message) : base(message) { }
    /// <summary>Creates a failure that keeps the problem's code and certainty for callers that act on them.</summary>
    /// <param name="problem">The catalogued problem; its message becomes the exception message.</param>
    public StackerException(Framework.Problem problem) : base(problem.Message) => Problem = problem;
}
