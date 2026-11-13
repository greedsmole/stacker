using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class HistoryWorkspaceViewModel
{
    private sealed record LayerLoad(int Generation, CancellationTokenSource Cancellation);
}
