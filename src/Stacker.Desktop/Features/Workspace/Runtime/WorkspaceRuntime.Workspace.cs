using Stacker.Core;
using Stacker.Core.Framework;
using System.Collections.ObjectModel;
namespace Stacker.Desktop.ViewModels;
internal sealed partial class WorkspaceRuntime
{
    internal WorkspaceState _workspace = new();
    internal WorkspacePersistence _workspacePersistence = default!;
    internal Task _workspaceSave = Task.CompletedTask;
    internal Dictionary<string, ReviewViewModel> _reviews = [];
    internal LinkedList<string> _reviewOrder = new();
    internal SemaphoreSlim _reviewGate = new(1, 1);
    internal SemaphoreSlim _syncGate = new(1, 1);
    internal string? _displayedStateKey = default!;
    internal bool _applyingChanges = default!;
    internal GitHubSnapshot? _pendingRemote { get => Session.Pending?.Remote; set { if(value is null) Session.DiscardPending(); else Session.Offer(value, _historyRemoteGraph); } }
    internal bool _pendingLocal = default!;
    internal RefreshCoordinator? _coordinator = default!;
    internal bool _windowActive = default!;
}
