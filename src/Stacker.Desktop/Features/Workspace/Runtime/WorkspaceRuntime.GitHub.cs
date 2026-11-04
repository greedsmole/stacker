using Stacker.Core;
using Stacker.Core.Framework;
using System.Collections.ObjectModel;
namespace Stacker.Desktop.ViewModels;
internal sealed partial class WorkspaceRuntime
{
    internal IGitHubReader? _github = default!;
    internal IGitHubWriter? _writer = default!;
    internal IPullRequestObjects? _prObjects = default!;
    internal IApplicationState _applicationStore = default!;
    internal IDemoRepository? _demo = default!;
    internal IGhToolSettings? _ghExecutable = default!;
    internal GraphSnapshot? _historyRemoteGraph { get=>Session.Displayed.Graph; set=>Session.SetRemote(Session.Displayed.Remote,value); }
    internal GitHubSnapshot? _remoteSnapshot { get => Session.Displayed.Remote; set => Session.SetRemote(value, _historyRemoteGraph); }
    internal CancellationTokenSource? _githubLoad = default!;
}
