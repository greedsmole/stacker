namespace StackerDemo;

public sealed record Payment(decimal Amount)
{
    public string Currency { get; init; } = "USD";
    public bool IsValid => Amount > 0;
}
