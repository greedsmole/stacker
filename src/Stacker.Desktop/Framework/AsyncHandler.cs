using Avalonia.Controls;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;

/// <summary>The only async-void boundary; all event failures become visible UI outcomes.</summary>
internal static class AsyncHandler
{
    public static async void Run(Func<Task> action, Action<string> report)
    {
        try { await action(); }
        catch (OperationCanceledException) { report("The action was cancelled."); }
        catch (Exception ex) { report(ex.Message); }
    }

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
