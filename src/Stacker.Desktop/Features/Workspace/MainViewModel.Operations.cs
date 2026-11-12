using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public ObservableCollection<CommandOutcome> OperationItems { get; } = [];
    [ObservableProperty] private bool _isOperationsOpen;
    [RelayCommand]
    private void ShowOperations()
    {
        OperationItems.Clear();
        var scopes = new[] { _runtime._repository?.Root, _runtime._remoteSnapshot is { } remote ? "github:" + remote.Context.Identity : null };
        foreach (var scope in scopes.OfType<string>())
        {
            var records = _runtime._commands?.Recent(scope);
            if (records?.IsFailed == true) { Error = records.Problem!.Message; continue; }
            foreach (var entry in records?.Value ?? [])
            {
                if (OperationItems.Any(item => item.OperationId == entry.Id)) continue;
                OperationItems.Add(new(entry.Phase == JournalPhase.Applied ? CommandStatus.Applied : entry.NeedsAttention ? CommandStatus.NeedsUser : CommandStatus.Failed,
                    entry.Summary, null)
                { OperationId = entry.Id, Scope = scope, Effects = entry.Steps.Select(step => $"{step.Kind} · {step.Target} · {step.State}").ToArray() });
            }
        }
        while (OperationItems.Count > 40) OperationItems.RemoveAt(OperationItems.Count - 1);
        IsOperationsOpen = true;
    }
    [RelayCommand] private void CloseOperations() => IsOperationsOpen = false;
    public async Task ExportDiagnosticsAsync(string folder)
    {
        if (_runtime.Diagnostics is null) { Error = "Diagnostics are unavailable."; return; }
        (await _runtime.Diagnostics.ExportAsync(folder)).OrThrow(); Status = "Diagnostic metadata exported. Source code and comments were excluded.";
    }
    public async Task RelocateRepositoryAsync(RepositoryId repository, string root, WorktreeId? worktree = null)
    {
        if (_runtime.Identities is null) throw new StackerException("Repository identity is unavailable.");
        var previous = (await _runtime.Identities.KnownAsync()).OrThrow().FirstOrDefault(r => r.Repository == repository && (worktree is null || r.Worktree == worktree));
        var relocated = (await _runtime.Identities.RelocateAsync(repository, root, worktree)).OrThrow();
        if (previous is not null && previous.Root != relocated.Root)
        {
            var existing = (await _runtime._applicationStore.ReadAsync<RepositoryPreferences>("repositories", relocated.Root)).OrThrow();
            var preferences = (await _runtime._applicationStore.ReadAsync<RepositoryPreferences>("repositories", previous.Root)).OrThrow();
            if (existing is null && preferences is not null) (await _runtime._applicationStore.WriteAsync("repositories", relocated.Root, preferences)).OrThrow();
        }
        await OpenRepositoryAsync(relocated.Root);
    }
    public async Task<IReadOnlyList<RepositoryIdentity>> KnownRepositoriesAsync() => _runtime.Identities is null ? [] : (await _runtime.Identities.KnownAsync()).OrThrow();
}
