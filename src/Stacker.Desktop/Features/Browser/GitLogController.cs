using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

/// <summary>Maintains the bounded diagnostic Git-command log exposed by the workspace shell.</summary>
/// <remarks>Background notifications are marshalled to Avalonia's UI thread. Entries are replaced by id and capped at 500
/// so diagnostics cannot grow without bound during a long session.</remarks>
internal sealed class GitLogController(IGitLogPresentation owner, WorkspaceRuntime runtime)
{
    /// <summary>Adds or replaces one entry on the UI thread, or clears the view when logging is reset.</summary>
    /// <param name="entry">Changed entry, or null to clear all visible entries.</param>
    public void LogChanged(GitCommandEntry? entry) => Dispatcher.UIThread.Post(() =>
    {
        if (runtime._disposed) return;
        if (entry is null) { owner.GitLogEntries.Clear(); return; }
        var index = owner.GitLogEntries.ToList().FindIndex(item => item.Id == entry.Id);
        if (index >= 0) owner.GitLogEntries[index] = entry;
        else owner.GitLogEntries.Add(entry);
        if (owner.GitLogEntries.Count > 500) owner.GitLogEntries.RemoveAt(0);
    });

    /// <summary>Clears the command log retained by the runtime.</summary>
    public void ClearGitLog() => runtime._commandLog?.Clear();

}
