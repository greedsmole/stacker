using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;

public partial class MainWindow
{
    private enum ClosePhase
    {
        Open, Preparing, AwaitingDecision, SavingCopy, Committing, ReadyToClose
    }
}
