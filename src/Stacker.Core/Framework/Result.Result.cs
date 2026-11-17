namespace Stacker.Core.Framework;

/// <summary>Helpers for results that carry nothing.</summary>
public static class Result
{
    /// <summary>A successful empty result for an effect that has no data to return.</summary>
    public static Result<Unit> Ok => Result<Unit>.Ok(Unit.Value);
    /// <summary>Creates a failed empty result from a code, message and optional details.</summary>
    /// <param name="code">A catalogued code.</param>
    /// <param name="message">Display-ready text.</param>
    /// <param name="details">Optional raw supporting lines.</param>
    /// <returns>A failed result with unknown certainty.</returns>
    public static Result<Unit> Fail(string code, string message, params string[] details) => Result<Unit>.Fail(code, message, details);
    /// <summary>Runs an action and converts any exception into a problem with the given code.</summary>
    /// <remarks>Cancellation is rethrown so a cancelled operation is not reported as an ordinary failure; every
    /// other exception becomes a problem whose single detail is the exception message. The exception type is
    /// deliberately lost at this boundary; callers act on the code, not the CLR type.</remarks>
    /// <param name="action">The fallible action.</param>
    /// <param name="code">The catalogued code to use when the action throws.</param>
    /// <param name="message">Display-ready text for the converted failure.</param>
    /// <returns>A successful empty result, or the converted failure.</returns>
    public static async Task<Result<Unit>> CaptureAsync(Func<Task> action, string code, string message)
    {
        try
        {
            await action();
            return Ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<Unit>.Fail(code, message, ex.Message); }
    }
}
