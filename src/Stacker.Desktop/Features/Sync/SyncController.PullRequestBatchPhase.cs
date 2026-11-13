using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class SyncController
{
    private enum PullRequestBatchPhase
    {
        CheckingPlan, PreparingLayer, SendingLayer, Refreshing
    }
}
