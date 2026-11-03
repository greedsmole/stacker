using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    internal readonly CommandBus? _commands;
    internal void CommandStarting()
    {
        _refresh?.Cancel(); _comparison?.Cancel(); _githubLoad?.Cancel();
    }
    internal void ReportCommand(CommandOutcome outcome)
    {
        if (_disposed) return;
        Status = outcome.Message;
        if (!outcome.Ok && !outcome.RolledBack) Error = outcome.Message;
    }
}
