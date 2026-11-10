using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceRuntime
{
    internal IChangeIdentityService? Identities;
    internal IDiagnosticSink? Diagnostics;
    internal IGitRepositoryReader _git = default!;
    internal IStackStore _store = default!;
    internal ISettingsStore _settingsStore = default!;
    internal IGitToolSettings _executable = default!;
    internal StackService _stacks = default!;
    internal DiffService _diff = default!;
    internal ISyntaxHighlighter? _highlighter = default!;
    internal StackDocument? _document = default!;
    internal RepositorySnapshot? _repository { get => Session.Displayed.Repository; set { if (value is not null) Session.Open(value); } }
    internal CancellationTokenSource? _refresh = default!;
    internal CancellationTokenSource? _comparison = default!;
    internal bool _suppress = default!;
    internal bool _disposed = default!;
    internal StackGroupViewModel? _activeGroup = default!;
    internal Dictionary<string, List<DiffSectionViewModel>> _views = [];
    internal int[] _selectedPositions = [];
}
