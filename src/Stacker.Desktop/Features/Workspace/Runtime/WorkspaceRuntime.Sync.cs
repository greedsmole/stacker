using Stacker.Core;
using Stacker.Core.Framework;
using System.Collections.ObjectModel;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceRuntime
{
    internal IGitBranchWriter? _branchWriter = default!;
    internal IGitHubPullRequestWriter? _pullRequestWriter = default!;
}
