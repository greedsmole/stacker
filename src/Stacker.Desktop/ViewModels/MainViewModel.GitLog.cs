using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Infrastructure;

namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly GitCommandLog? _commandLog;
    public ObservableCollection<GitCommandEntry> GitLogEntries { get; } = [];
    [ObservableProperty] private bool _isGitLogOpen;
    public bool IsChangesOpen => !IsGitLogOpen;
    partial void OnIsGitLogOpenChanged(bool value) => OnPropertyChanged(nameof(IsChangesOpen));
    private void LogChanged(GitCommandEntry? entry) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        if (entry is null) { GitLogEntries.Clear(); return; }
        var index = GitLogEntries.ToList().FindIndex(item => item.Id == entry.Id);
        if (index >= 0) GitLogEntries[index] = entry;
        else GitLogEntries.Add(entry);
        if (GitLogEntries.Count > 500) GitLogEntries.RemoveAt(0);
    });
    [RelayCommand] private void ClearGitLog() => _commandLog?.Clear();
}
