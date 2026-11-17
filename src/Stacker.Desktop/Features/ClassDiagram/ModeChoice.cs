namespace Stacker.Desktop;

internal sealed record ModeChoice(DiagramMode Mode, string Text)
{
    public override string ToString() => Text;
}
