using System.Collections.ObjectModel;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceRuntime
{
    internal IHistoryStore? HistoryStore = default!;
    internal IHistoryGit? HistoryObjects = default!;
    internal ChangeGraphService? HistoryGraph = default!;
    internal VersionComparisonService? HistoryComparisons = default!;
    internal ReviewProgressService? HistoryProgress = default!;
}
