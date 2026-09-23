namespace StackerDemo;

public sealed record Session
{
    public string User { get; init; } = "alice";
    public bool IsAuthenticated => User != "guest";
}
