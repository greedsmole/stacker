// Purpose: View-model actions that expose recent Git and GitHub CLI diagnostics.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;


namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Recent commands, newest first.</summary>
    public ObservableCollection<GitCommandEntry> GitLogEntries { get; } = [];
    public ObservableCollection<GitCommandEntry> GitHubLogEntries { get; } = [];
    public ObservableCollection<GitCommandEntry> VisibleGitLogEntries => GitHubLogsOnly ? GitHubLogEntries : GitLogEntries;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VisibleGitLogEntries))]
    private bool _gitHubLogsOnly;
    /// <summary>Whether the command log is visible.</summary>
    [ObservableProperty] internal bool _isGitLogOpen;
    /// <summary>Whether the normal changes view is visible instead of the Git log.</summary>
    public bool IsChangesOpen => !IsGitLogOpen;
    /// <summary>Updates the derived changes-view visibility when the log tab changes.</summary>
    partial void OnIsGitLogOpenChanged(bool value) => OnPropertyChanged(nameof(IsChangesOpen));
    /// <summary>Forwards a Git command notification to the UI-thread log controller.</summary>
    public void LogChanged(GitCommandEntry? entry) => _gitLogController.LogChanged(entry);
    [RelayCommand]
    private void ShowGitHubLog()
    {
        GitHubLogsOnly = true;
        IsGitLogOpen = true;
    }
    /// <summary>Removes all retained Git diagnostic entries.</summary>
    [RelayCommand] internal void ClearGitLog() => _gitLogController.ClearGitLog();
}
