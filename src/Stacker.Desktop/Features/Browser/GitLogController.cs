using System.Collections.ObjectModel;
using Avalonia.Threading;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

/// <summary>Maintains the bounded command log exposed by the workspace shell.</summary>
/// <remarks>Background notifications are marshalled to Avalonia's UI thread. Entries are replaced by id and capped at 500
/// so diagnostics cannot grow without bound during a long session.</remarks>
internal sealed class GitLogController(IGitLogPresentation owner, WorkspaceRuntime runtime)
{
    /// <summary>Adds or replaces one entry on the UI thread, or clears the view when logging is reset.</summary>
    /// <param name="entry">Changed entry, or null to clear all visible entries.</param>
    public void LogChanged(GitCommandEntry? entry) => Dispatcher.UIThread.Post(() =>
    {
        if (runtime._disposed)
            return;
        if (entry is null)
        {
            owner.GitLogEntries.Clear();
            owner.GitHubLogEntries.Clear();
        }
        else
            AddOrUpdate(entry);
    });

    internal void AddOrUpdate(GitCommandEntry entry)
    {
        Upsert(owner.GitLogEntries, entry);
        if (entry.IsGitHub)
            Upsert(owner.GitHubLogEntries, entry);
        if (owner.GitLogEntries.Count <= 500)
            return;
        var evicted = owner.GitLogEntries[^1];
        owner.GitLogEntries.RemoveAt(owner.GitLogEntries.Count - 1);
        if (evicted.IsGitHub)
        {
            var index = Find(owner.GitHubLogEntries, evicted.Id);
            if (index >= 0)
                owner.GitHubLogEntries.RemoveAt(index);
        }
    }

    private static void Upsert(ObservableCollection<GitCommandEntry> entries, GitCommandEntry entry)
    {
        var index = Find(entries, entry.Id);
        if (index >= 0)
            entries[index] = entry;
        else
            entries.Insert(0, entry);
    }

    private static int Find(ObservableCollection<GitCommandEntry> entries, Guid id)
    {
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Id == id)
                return i;
        return -1;
    }

    /// <summary>Clears the command log retained by the runtime.</summary>
    public void ClearGitLog() => runtime._commandLog?.Clear();

}
