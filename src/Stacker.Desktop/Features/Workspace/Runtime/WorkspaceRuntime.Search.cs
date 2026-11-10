using Stacker.Core;
using Stacker.Core.Framework;
using System.Collections.ObjectModel;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceRuntime
{
    internal CancellationTokenSource? _searchLoad = default!;
}
