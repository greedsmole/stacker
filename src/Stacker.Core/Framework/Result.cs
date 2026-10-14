namespace Stacker.Core.Framework;

/// <summary>The single value passed around when a call has no result of its own.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

/// <summary>What went wrong at a boundary, with a stable code the UI can act on.</summary>
/// <remarks>Messages are ready for display; details carry the raw tool output for the diagnostics tab.</remarks>
public sealed record Problem(string Code, string Message, IReadOnlyList<string> Details)
{
    public static Problem Of(string code, string message, params string[] details) => new(code, message, details);
    public override string ToString() => Code + ": " + Message;
}

/// <summary>The closed set of problem codes; keep it closed so callers can switch on it.</summary>
public static class ProblemCodes
{
    public const string Unknown = "app.unknown";
    public const string GitMissing = "git.missing";
    public const string GitFailed = "git.failed";
    public const string GitTimeout = "git.timeout";
    public const string GitRefMissing = "git.ref-missing";
    public const string GitNotLocalBranch = "git.not-local-branch";
    public const string GitDirtyTree = "git.dirty-tree";
    public const string GitOperationInProgress = "git.operation-in-progress";
    public const string GitConflict = "git.conflict";
    public const string StoreRead = "store.read";
    public const string StoreWrite = "store.write";
    public const string StoreCorrupt = "store.corrupt";
    public const string StoreBusy = "store.busy";
    public const string StoreStaleRevision = "store.stale-revision";
    public const string StateInvalid = "state.invalid";
    public const string EditorMissing = "editor.missing";
    public const string GitHubAuth = "github.auth";
    public const string GitHubAccess = "github.access";
    public const string GitHubRequest = "github.request";
    public const string GitHubPayload = "github.payload";
    public const string PublishStalePlan = "publish.stale-plan";
}

/// <summary>A value or a problem; boundary calls return this instead of throwing across the port.</summary>
/// <remarks>Reference type on purpose: an unguarded default is a null reference, not a silent success.</remarks>
public sealed class Result<T>
{
    public T? Value { get; }
    public Problem? Problem { get; }
    public bool IsOk { get; }
    public bool IsFailed => !IsOk;
    private Result(bool ok, T? value, Problem? problem) { IsOk = ok; Value = value; Problem = problem; }
    public static Result<T> Ok(T value) => new(true, value, null);
    public static Result<T> Fail(Problem problem) => new(false, default, problem);
    public static Result<T> Fail(string code, string message, params string[] details) => new(false, default, Problem.Of(code, message, details));
    /// <summary>Transforms a successful value; a problem passes through untouched.</summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> map) => IsOk ? Result<TOut>.Ok(map(Value!)) : Result<TOut>.Fail(Problem!);
    /// <summary>Chains a further fallible step without nesting results.</summary>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> next) => IsOk ? next(Value!) : Result<TOut>.Fail(Problem!);
    /// <summary>Uses the value or throws; only for call sites where a problem is a programming error.</summary>
    public T OrThrow() => IsOk ? Value! : throw new StackerException(Problem!.Message);
}

/// <summary>Helpers for results that carry nothing.</summary>
public static class Result
{
    public static Result<Unit> Ok => Result<Unit>.Ok(Unit.Value);
    public static Result<Unit> Fail(string code, string message, params string[] details) => Result<Unit>.Fail(code, message, details);
    /// <summary>Runs an action and converts any exception into a problem with the given code.</summary>
    public static async Task<Result<Unit>> CaptureAsync(Func<Task> action, string code, string message)
    {
        try { await action(); return Ok; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<Unit>.Fail(code, message, ex.Message); }
    }
}
