// Purpose: View-model actions that expose recent Git command diagnostics in the dedicated log tab.
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;


namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Recent Git commands in display order.</summary>
    public ObservableCollection<GitCommandEntry> GitLogEntries { get; } = [];
    /// <summary>Whether the dedicated Git diagnostic log is visible.</summary>
    [ObservableProperty] internal bool _isGitLogOpen;
    /// <summary>Whether the normal changes view is visible instead of the Git log.</summary>
    public bool IsChangesOpen => !IsGitLogOpen;
    /// <summary>Updates the derived changes-view visibility when the log tab changes.</summary>
    partial void OnIsGitLogOpenChanged(bool value) => OnPropertyChanged(nameof(IsChangesOpen));
    /// <summary>Forwards a Git command notification to the UI-thread log controller.</summary>
    public void LogChanged(GitCommandEntry? entry) => _gitLogController.LogChanged(entry);
    /// <summary>Removes all retained Git diagnostic entries.</summary>
    [RelayCommand] internal void ClearGitLog() => _gitLogController.ClearGitLog();
}
