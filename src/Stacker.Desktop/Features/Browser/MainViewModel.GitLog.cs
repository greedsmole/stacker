// Purpose: View-model actions that expose recent Git command diagnostics in the dedicated log tab.
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;


namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    internal readonly IGitDiagnosticLog? _commandLog;
    public ObservableCollection<GitCommandEntry> GitLogEntries { get; } = [];
    [ObservableProperty] internal bool _isGitLogOpen;
    public bool IsChangesOpen => !IsGitLogOpen;
    partial void OnIsGitLogOpenChanged(bool value) => OnPropertyChanged(nameof(IsChangesOpen));
    internal void LogChanged(GitCommandEntry? entry) => _gitLogController.LogChanged(entry);
    [RelayCommand] internal void ClearGitLog() => _gitLogController.ClearGitLog();
}
