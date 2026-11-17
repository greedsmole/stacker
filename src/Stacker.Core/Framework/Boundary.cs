namespace Stacker.Core.Framework;

/// <summary>Translates legacy adapter implementations to typed ports without exposing OS exceptions.</summary>
/// <remarks>An adapter that still throws keeps its existing shape; the port method wraps that call in one of the
/// <c>Capture</c> overloads, so the caller receives a value or a catalogued <see cref="Problem"/> instead of an
/// exception. Cancellation is the one exception rethrown unchanged: a cancelled command is a distinct outcome, not a
/// failure to report. A caller whose own boundary already catches exceptions can unwrap a result again with
/// <see cref="UnwrapAsync{T}"/>.</remarks>
public static class Boundary
{
    /// <summary>Maps a caught exception to the <see cref="Problem"/> a port returns in its place.</summary>
    /// <param name="error">The exception caught at the adapter boundary.</param>
    /// <param name="code">The catalogued <see cref="ProblemCodes"/> value that identifies this port's failure.</param>
    /// <returns>The problem to report. A <see cref="StackerException"/> that already carries a problem keeps its own
    /// code, message, details and <see cref="EffectCertainty"/>; the other arms build a new problem from the caller's
    /// code and the exception message.</returns>
    /// <remarks>The <see cref="StackerException"/> arm runs first, so a <see cref="GitHubAuthenticationException"/>
    /// built from a problem also keeps the lower layer's classification, such as
    /// <see cref="EffectCertainty.Rejected"/> for an HTTP 401 or 422. Only an authentication exception built from a
    /// bare message is relabelled as <see cref="ProblemCodes.GitHubAuth"/>. An exception without a problem contributes
    /// its message alone, never the raw tool output, and leaves certainty at the default
    /// <see cref="EffectCertainty.Unknown"/>; that default is what tells the command runner the effect may already
    /// have happened.</remarks>
    public static Problem Problem(Exception error, string code) => error switch
    {
        StackerException { Problem: { } problem } => problem,
        GitHubAuthenticationException => Framework.Problem.Of(ProblemCodes.GitHubAuth, error.Message),
        _ => Framework.Problem.Of(code, error.Message)
    };
    /// <summary>Runs an asynchronous adapter call and returns its value or a mapped problem.</summary>
    /// <param name="action">The adapter work, which may still throw its native exception.</param>
    /// <param name="code">The problem code used when the exception carries no catalogued problem of its own.</param>
    /// <typeparam name="T">The value produced on success.</typeparam>
    /// <returns>The value on success; otherwise a failed result carrying the translated problem.</returns>
    /// <exception cref="OperationCanceledException">Rethrown unchanged, so cancellation stays a dismissal signal for
    /// the command runner instead of becoming an ordinary failure that might be compensated.</exception>
    /// <remarks>Only non-cancellation exceptions become problems. Rethrowing cancellation is deliberate: the bus
    /// and runner use it to distinguish a user-cancelled command, which gets compensation and a Cancelled status,
    /// from a failed one, which gets a problem and the recovery path.</remarks>
    public static async Task<Result<T>> CaptureAsync<T>(Func<Task<T>> action, string code)
    {
        try
        {
            return Result<T>.Ok(await action());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<T>.Fail(Problem(ex, code)); }
    }
    /// <summary>Runs an asynchronous adapter call that produces no value and captures its failure.</summary>
    /// <param name="action">The adapter work, which may still throw its native exception.</param>
    /// <param name="code">The problem code used when the exception carries no catalogued problem of its own.</param>
    /// <returns>A successful <see cref="Unit"/> result, or the mapped failure.</returns>
    /// <exception cref="OperationCanceledException">Rethrown unchanged; the cancellation rule matches the generic
    /// overload.</exception>
    /// <remarks>Delegates to <see cref="CaptureAsync{T}(Func{Task{T}}, string)"/> with <see cref="Unit.Value"/>, so
    /// value-less ports share exactly the same translation.</remarks>
    public static Task<Result<Unit>> CaptureAsync(Func<Task> action, string code) =>
        CaptureAsync(async () => { await action(); return Unit.Value; }, code);
    /// <summary>Runs a synchronous adapter call and returns its value or a mapped problem.</summary>
    /// <param name="action">The adapter work, which may still throw its native exception.</param>
    /// <param name="code">The problem code used when the exception carries no catalogued problem of its own.</param>
    /// <typeparam name="T">The value produced on success.</typeparam>
    /// <returns>The value on success; otherwise a failed result carrying the translated problem.</returns>
    /// <exception cref="OperationCanceledException">Rethrown unchanged, matching the asynchronous overloads.</exception>
    /// <remarks>Used by port implementations whose work is synchronous, such as state reads and object-store
    /// budgets; the translation and cancellation rules are identical to the asynchronous overloads.</remarks>
    public static Result<T> Capture<T>(Func<T> action, string code)
    {
        try
        {
            return Result<T>.Ok(action());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<T>.Fail(Problem(ex, code)); }
    }
    /// <summary>Runs a synchronous adapter action that produces no value and captures its failure.</summary>
    /// <param name="action">The adapter work, which may still throw its native exception.</param>
    /// <param name="code">The problem code used when the exception carries no catalogued problem of its own.</param>
    /// <returns>A successful <see cref="Unit"/> result, or the mapped failure.</returns>
    /// <exception cref="OperationCanceledException">Rethrown unchanged; the cancellation rule matches the generic
    /// overload.</exception>
    /// <remarks>Delegates to <see cref="Capture{T}(Func{T}, string)"/> with <see cref="Unit.Value"/>.</remarks>
    public static Result<Unit> Capture(Action action, string code) => Capture(() => { action(); return Unit.Value; }, code);
    /// <summary>Awaits a port call and unwraps its value, throwing the carried problem instead of returning it.</summary>
    /// <param name="action">The port call expected to succeed.</param>
    /// <typeparam name="T">The value produced on success.</typeparam>
    /// <returns>The successful value.</returns>
    /// <exception cref="GitHubAuthenticationException">Thrown by <see cref="Result{T}.OrThrow"/> when the problem code
    /// is <see cref="ProblemCodes.GitHubAuth"/>, so callers can offer the sign-in path.</exception>
    /// <exception cref="StackerException">Thrown by <see cref="Result{T}.OrThrow"/> for any other problem.</exception>
    /// <remarks>This re-enters the exception flow for call sites whose surrounding boundary already catches and
    /// reports exceptions, and for internal helpers where the problem is fatal. It adds no translation: the original
    /// problem, code and message survive on the exception.</remarks>
    public static async Task<T> UnwrapAsync<T>(Task<Result<T>> action) => (await action).OrThrow();
}
