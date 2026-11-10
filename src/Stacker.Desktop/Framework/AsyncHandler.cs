using Avalonia.Controls;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;

/// <summary>The only async-void boundary; all event failures become visible UI outcomes.</summary>
internal static class AsyncHandler
{
    /// <summary>Runs an asynchronous UI action and reports cancellation or failure visibly.</summary>
    /// <param name="action">Operation raised by an event handler.</param><param name="report">UI error reporter.</param>
    public static async void Run(Func<Task> action, Action<string> report)
    {
        try { await action(); }
        catch (OperationCanceledException) { report("The action was cancelled."); }
        catch (Exception ex) { report(ex.Message); }
    }

    /// <summary>Routes an error to the nearest owning view model, falling back to stderr.</summary>
    /// <param name="control">Control whose data context owns the operation.</param><param name="message">Message to display.</param>
    public static void Report(Control control, string message)
    {
        switch (control.DataContext)
        {
            case MainViewModel shell: shell.Error = message; return;
            case ReviewViewModel review: review.Message = message; return;
            case DiffSectionViewModel diff: diff.Message = message; return;
        }
        if (TopLevel.GetTopLevel(control)?.DataContext is MainViewModel owner) owner.Error = message;
        else Console.Error.WriteLine(message);
    }
}
