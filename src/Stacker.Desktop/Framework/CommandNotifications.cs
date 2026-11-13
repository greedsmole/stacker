using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public void CommandStarting()
    {
        _runtime._refresh?.Cancel();
        _runtime._comparison?.Cancel();
        _runtime._githubLoad?.Cancel();
    }
    public void ReportCommand(CommandOutcome outcome)
    {
        if (_runtime._disposed)
            return;
        OperationItems.Insert(0, outcome);
        while (OperationItems.Count > 40)
            OperationItems.RemoveAt(OperationItems.Count - 1);
        Status = outcome.Message;
        if (!outcome.Ok && !outcome.RolledBack)
            Error = outcome.Message;
    }
}
