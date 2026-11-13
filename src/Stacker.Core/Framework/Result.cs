namespace Stacker.Core.Framework;

/// <summary>A value or a problem; boundary calls return this instead of throwing across the port.</summary>
/// <remarks>Reference type on purpose: an unguarded default is a null reference, not a silent success.
/// Instances are created only by the factories, so <see cref="IsOk"/>, <see cref="Value"/> and
/// <see cref="Result{T}.Problem"/> always agree: exactly one side is meaningful. A failed result exposes
/// <c>default(T)</c> in <see cref="Value"/> (0 for a number, <see langword="null"/> for a reference type), so callers
/// must check <see cref="IsOk"/> before reading it. The <c>Map</c> and <c>Bind</c> operators reuse the same
/// <c>Problem</c> instance, preserving its code, details and certainty. Cancellation is not represented;
/// it is rethrown as cancellation.</remarks>
/// <typeparam name="T">The successful value type.</typeparam>
public sealed class Result<T>
{
    /// <summary>The successful value, or <c>default(T)</c> when the result is failed; inspect
    /// <see cref="IsOk"/> first.</summary>
    public T? Value
    {
        get;
    }
    /// <summary>The failure, or <see langword="null"/> when the result is successful.</summary>
    public Problem? Problem
    {
        get;
    }
    /// <summary>True when the call produced a value.</summary>
    public bool IsOk
    {
        get;
    }
    /// <summary>The inverse of <see cref="IsOk"/>, provided so failure checks read naturally.</summary>
    public bool IsFailed => !IsOk;
    /// <summary>Private so every instance comes from <see cref="Ok"/> or one of the failure factories, which keeps
    /// the value/problem invariant true.</summary>
    /// <param name="ok">Whether this is a success.</param>
    /// <param name="value">The value for a success.</param>
    /// <param name="problem">The problem for a failure.</param>
    private Result(bool ok, T? value, Problem? problem)
    {
        IsOk = ok;
        Value = value;
        Problem = problem;
    }
    /// <summary>Creates a successful result carrying the given value.</summary>
    /// <param name="value">The value to carry; callers must not pass null to signal failure.</param>
    /// <returns>A successful result.</returns>
    public static Result<T> Ok(T value) => new(true, value, null);
    /// <summary>Creates a failed result from an existing problem, preserving its certainty.</summary>
    /// <param name="problem">The catalogued failure.</param>
    /// <returns>A failed result with no value.</returns>
    public static Result<T> Fail(Problem problem) => new(false, default, problem);
    /// <summary>Creates a failed result from a code, message and optional details.</summary>
    /// <param name="code">A catalogued code.</param>
    /// <param name="message">Display-ready text.</param>
    /// <param name="details">Optional raw supporting lines.</param>
    /// <returns>A failed result whose problem has unknown certainty.</returns>
    public static Result<T> Fail(string code, string message, params string[] details) => new(false, default, Problem.Of(code, message, details));
    /// <summary>Transforms a successful value; a problem passes through untouched.</summary>
    /// <remarks>The mapping function is not invoked on failure, and an exception it throws is not converted into a
    /// problem; this operator is for pure transformations.</remarks>
    /// <typeparam name="TOut">The transformed value type.</typeparam>
    /// <param name="map">The transformation applied only on success.</param>
    /// <returns>A successful result of the new type, or the original problem.</returns>
    public Result<TOut> Map<TOut>(Func<T, TOut> map) => IsOk ? Result<TOut>.Ok(map(Value!)) : Result<TOut>.Fail(Problem!);
    /// <summary>Chains a further fallible step without nesting results.</summary>
    /// <remarks>The next step is not invoked on failure, and the existing problem is returned as-is so its original
    /// code and details survive the chain.</remarks>
    /// <typeparam name="TOut">The next step's value type.</typeparam>
    /// <param name="next">The fallible step applied only on success.</param>
    /// <returns>The next step's result, or the original problem.</returns>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> next) => IsOk ? next(Value!) : Result<TOut>.Fail(Problem!);
    /// <summary>Uses the value or throws; only for call sites where a problem is a programming error.</summary>
    /// <remarks>This bridges <see cref="Result{T}"/> back into the exception world at reporting boundaries, which
    /// already know how to display a <see cref="StackerException"/>. Authentication gets a distinct exception so a
    /// refresh boundary can suspend the account instead of reporting a generic error; every other code becomes a
    /// <see cref="StackerException"/> that still carries the problem.</remarks>
    /// <returns>The successful value.</returns>
    /// <exception cref="GitHubAuthenticationException">The problem code is
    /// <see cref="ProblemCodes.GitHubAuth"/>.</exception>
    /// <exception cref="StackerException">Any other failure; the problem is available on the exception.</exception>
    public T OrThrow()
    {
        if (IsOk)
            return Value!;
        if (Problem!.Code == ProblemCodes.GitHubAuth)
            throw new GitHubAuthenticationException(Problem);
        throw new StackerException(Problem);
    }
}
