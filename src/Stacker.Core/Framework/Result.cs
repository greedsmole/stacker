namespace Stacker.Core.Framework;

/// <summary>The single value passed around when a call has no result of its own.</summary>
public readonly record struct Unit
{
    /// <summary>The only value the type has; exists so a void-like effect can still return
    /// <see cref="Result{T}"/>.</summary>
    public static readonly Unit Value = default;
}

/// <summary>How sure a boundary is that an effect reached its destination.</summary>
/// <remarks>This is the fact that decides whether a failed operation may be retried. A never-started or rejected
/// write did not land, a confirmed one did, and an unknown one may have landed and must be reconciled by inspecting
/// the destination. <see cref="Problem.Certainty"/> defaults to unknown, and the command outcome factories set only
/// the value they can prove, so an unclassified failure is never presented as safe to retry.</remarks>
public enum EffectCertainty
{
    /// <summary>No effect was attempted: the command was refused, or cancellation happened before dispatch.</summary>
    NotStarted,
    /// <summary>The destination answered with a definitive refusal (for example a GitHub 401, 403, 404 or 422), so
    /// the write did not land; fixing the cause and retrying is safe.</summary>
    Rejected,
    /// <summary>The effect is known to have landed.</summary>
    Confirmed,
    /// <summary>The effect was dispatched but the answer was lost, so it may or may not have landed. Never retried
    /// automatically; a reload or the user must reconcile it.</summary>
    Unknown
}

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

/// <summary>The closed set of problem codes; keep it closed so callers can switch on it.</summary>
/// <remarks>Codes are persisted in diagnostics and matched by the UI and tests, so a code should be added for a new
/// failure class rather than renamed or reused. The naming scheme is <c>area.reason</c>, lowercase with hyphens.
/// The catalogue is intentionally coarser than individual messages: the message explains one occurrence, the code
/// classifies it.</remarks>
public static class ProblemCodes
{
    /// <summary>No more specific code applies; the fallback for an unexpected failure.</summary>
    public const string Unknown = "app.unknown";
    /// <summary>The git executable could not be located.</summary>
    public const string GitMissing = "git.missing";
    /// <summary>Git ran but exited with a non-zero status.</summary>
    public const string GitFailed = "git.failed";
    /// <summary>Git did not finish within its process timeout.</summary>
    public const string GitTimeout = "git.timeout";
    /// <summary>The requested ref does not exist in the repository.</summary>
    public const string GitRefMissing = "git.ref-missing";
    /// <summary>The ref is not under <c>refs/heads/</c> and therefore cannot be rewritten.</summary>
    public const string GitNotLocalBranch = "git.not-local-branch";
    /// <summary>Uncommitted tracked changes block the operation.</summary>
    public const string GitDirtyTree = "git.dirty-tree";
    /// <summary>A rebase, merge or other working operation is already in progress.</summary>
    public const string GitOperationInProgress = "git.operation-in-progress";
    /// <summary>Conflicts must be resolved before the operation can continue.</summary>
    public const string GitConflict = "git.conflict";
    /// <summary>A state document could not be read.</summary>
    public const string StoreRead = "store.read";
    /// <summary>A state document could not be written.</summary>
    public const string StoreWrite = "store.write";
    /// <summary>A persisted document is damaged; the adapter may have quarantined it.</summary>
    public const string StoreCorrupt = "store.corrupt";
    /// <summary>Another instance holds the lock for this resource.</summary>
    public const string StoreBusy = "store.busy";
    /// <summary>The document changed after it was read, so saving would overwrite someone else's edit.</summary>
    public const string StoreStaleRevision = "store.stale-revision";
    /// <summary>A value or a persisted document violates its contract.</summary>
    public const string StateInvalid = "state.invalid";
    /// <summary>No editor command is configured, or it cannot be parsed.</summary>
    public const string EditorMissing = "editor.missing";
    /// <summary>GitHub authentication failed or the active credential is invalid.</summary>
    public const string GitHubAuth = "github.auth";
    /// <summary>GitHub refused the operation for lack of access.</summary>
    public const string GitHubAccess = "github.access";
    /// <summary>A GitHub request failed at the transport or HTTP level; certainty says whether it may still have
    /// landed.</summary>
    public const string GitHubRequest = "github.request";
    /// <summary>A GitHub response was missing or did not match the expected shape.</summary>
    public const string GitHubPayload = "github.payload";
    /// <summary>The approved publication plan no longer matches the current repository or pull request.</summary>
    public const string PublishStalePlan = "publish.stale-plan";
}

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
    public T? Value { get; }
    /// <summary>The failure, or <see langword="null"/> when the result is successful.</summary>
    public Problem? Problem { get; }
    /// <summary>True when the call produced a value.</summary>
    public bool IsOk { get; }
    /// <summary>The inverse of <see cref="IsOk"/>, provided so failure checks read naturally.</summary>
    public bool IsFailed => !IsOk;
    /// <summary>Private so every instance comes from <see cref="Ok"/> or one of the failure factories, which keeps
    /// the value/problem invariant true.</summary>
    /// <param name="ok">Whether this is a success.</param>
    /// <param name="value">The value for a success.</param>
    /// <param name="problem">The problem for a failure.</param>
    private Result(bool ok, T? value, Problem? problem) { IsOk = ok; Value = value; Problem = problem; }
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
        if (IsOk) return Value!;
        if (Problem!.Code == ProblemCodes.GitHubAuth) throw new GitHubAuthenticationException(Problem);
        throw new StackerException(Problem);
    }
}

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
        try { await action(); return Ok; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<Unit>.Fail(code, message, ex.Message); }
    }
}
