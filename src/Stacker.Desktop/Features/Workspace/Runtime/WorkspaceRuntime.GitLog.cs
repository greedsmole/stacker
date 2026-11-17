using System.Collections.ObjectModel;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceRuntime
{
    internal IGitDiagnosticLog? _commandLog = default!;
}
