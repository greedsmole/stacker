namespace Stacker.Core.Framework;

public sealed partial class CommandBus
{
    /// <summary>Admission policy, independent of whether the active command has drained.</summary>
    private enum Admission
    {
        Accepting, Paused, Stopped
    }
}
