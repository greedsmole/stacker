using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

internal sealed class GitLogController(IGitLogPresentation owner, WorkspaceRuntime runtime)
{
    public void LogChanged(GitCommandEntry? entry) => Dispatcher.UIThread.Post(() =>
    {
        if (runtime._disposed) return;
        if (entry is null) { owner.GitLogEntries.Clear(); return; }
        var index = owner.GitLogEntries.ToList().FindIndex(item => item.Id == entry.Id);
        if (index >= 0) owner.GitLogEntries[index] = entry;
        else owner.GitLogEntries.Add(entry);
        if (owner.GitLogEntries.Count > 500) owner.GitLogEntries.RemoveAt(0);
    });

    public void ClearGitLog() => runtime._commandLog?.Clear();

}
