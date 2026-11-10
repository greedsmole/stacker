using Stacker.Core;
using Stacker.Core.Framework;
using System.Collections.ObjectModel;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceRuntime
{
    internal IHistoryStore? HistoryStore = default!;
    internal IHistoryGit? HistoryObjects = default!;
    internal ChangeGraphService? HistoryGraph = default!;
    internal VersionComparisonService? HistoryComparisons = default!;
    internal ReviewProgressService? HistoryProgress = default!;
}
