namespace Stacker.Core.Framework;

/// <summary>Translates legacy adapter implementations to typed ports without exposing OS exceptions.</summary>
public static class Boundary
{
    public static Problem Problem(Exception error, string code) => error switch
    {
        GitHubAuthenticationException => Framework.Problem.Of(ProblemCodes.GitHubAuth, error.Message),
        StackerException { Problem: { } problem } => problem,
        _ => Framework.Problem.Of(code, error.Message)
    };
    public static async Task<Result<T>> CaptureAsync<T>(Func<Task<T>> action, string code)
    {
        try { return Result<T>.Ok(await action()); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<T>.Fail(Problem(ex, code)); }
    }
    public static Task<Result<Unit>> CaptureAsync(Func<Task> action, string code) =>
        CaptureAsync(async () => { await action(); return Unit.Value; }, code);
    public static Result<T> Capture<T>(Func<T> action, string code)
    {
        try { return Result<T>.Ok(action()); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<T>.Fail(Problem(ex, code)); }
    }
    public static Result<Unit> Capture(Action action, string code) => Capture(() => { action(); return Unit.Value; }, code);
    public static async Task<T> UnwrapAsync<T>(Task<Result<T>> action) => (await action).OrThrow();
}
