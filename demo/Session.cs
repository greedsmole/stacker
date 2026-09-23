namespace StackerDemo;

public sealed record Session
{
    public string User { get; init; } = "alice";
    public bool IsAuthenticated => User != "guest";
    public string DisplayName => IsAuthenticated ? $"Hello, {User}!" : "Sign in";
}
