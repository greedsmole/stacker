using System.Collections.ObjectModel;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceRuntime
{
    internal IGitBranchWriter? _branchWriter = default!;
    internal IGitHubPullRequestWriter? _pullRequestWriter = default!;
}
