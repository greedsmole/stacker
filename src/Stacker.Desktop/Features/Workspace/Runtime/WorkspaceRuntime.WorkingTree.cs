using Stacker.Core;
using Stacker.Core.Framework;
using System.Collections.ObjectModel;
namespace Stacker.Desktop.ViewModels;
internal sealed partial class WorkspaceRuntime
{
    internal IEditorLauncher? _editorLauncher = default!;
    internal string? _pendingStash = default!;
    internal JournalEntry? _activeEntry = default!;
}
