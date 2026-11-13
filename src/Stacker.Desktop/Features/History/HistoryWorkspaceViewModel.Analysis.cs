using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class HistoryWorkspaceViewModel
{
    [RelayCommand]
    private async Task AnalyzeAsync() => await ExecuteAsync(async () =>
    {
        ClearDocument();
        var result = await new StackAnalysisService(_store, _objects, _git).AnalyzeAsync(_repository, Graph, _lifetime.Token);
        Preview = string.Join("\n\n", result.Issues.Select(i => $"{Graph.Nodes.First(n => n.Id == i.ChangeId).Title} · {i.Kind}\n{i.Message}\n{string.Join("\n", i.Paths)}"));
        Message = result.Issues.Count == 0 ? "No issues found in the captured edges. This does not prove semantic correctness." : $"{result.Issues.Count} observations for this graph snapshot.";
    });
    [RelayCommand]
    private async Task AttentionAsync() => await ExecuteAsync(async () =>
    {
        ClearDocument();
        Attention.Clear();
        foreach (var (node, version) in _path.DistinctBy(p => p.Node.Id))
        {
            var files = (await _git.FilesAsync(_root, version.MergeBase!, version.HeadSha, _lifetime.Token)).OrThrow();
            var statuses = await _progress.StatusAsync(_repository, Graph.Scope, version, files, _lifetime.Token);
            foreach (var status in statuses.Where(f => f.State != FileReviewState.Reviewed || f.Concern is not null))
                Attention.Add(new(node.Id, status.Change.NewPath, node.Title + " · " + status.Label, status.Concern is not null));
            foreach (var concern in (await _store.ConcernsAsync(node.Id, _lifetime.Token)).OrThrow().Where(c => !statuses.Any(s => s.Change.NewPath == c.Path)))
                Attention.Add(new(node.Id, concern.Path, node.Title + " · " + concern.Path + " · BLOCKER on former file: " + concern.Text, true));
        }
        Preview = string.Join("\n", Attention);
        Message = $"{Attention.Count} files require attention, in dependency order.";
    });
    [RelayCommand(CanExecute = nameof(CanReviewFile))]
    private async Task EvolutionAsync()
    {
        if (SelectedFile is not { } selected)
            return;
        var generation = _generation;
        await ExecuteAsync(async () =>
        {
            var evolution = await new FileEvolutionService(_git).BuildAsync(_root, _path, selected.Change.NewPath, _lifetime.Token);
            var text = new System.Text.StringBuilder();
            var sections = new List<DiffDocumentSection>();
            foreach (var step in evolution.Steps)
            {
                text.AppendLine($"{step.Change.Title} · {step.Version.Label}" + (step.Continuous ? "" : " · ancestry gap — independent diff"));
                if (step.File is { } file)
                    sections.Add(new(step.Change.Title, _root, step.Version.MergeBase!, step.Version.HeadSha, [file]));
                else
                    text.AppendLine("No file change in this layer.");
                if (_discussion is not null && step.Change.PullRequest is not null && step.File is { } changed)
                {
                    try
                    {
                        var discussion = await _discussion(step.Change, step.Version, _lifetime.Token);
                        foreach (var thread in discussion?.Threads.Where(t => t.Path == changed.NewPath || t.Path == changed.OldPath) ?? [])
                            text.AppendLine($"Original PR #{step.Change.PullRequest} · {thread.Label}\n" + string.Join("\n", thread.Comments.Select(c => c.Author + ": " + c.DisplayBody)));
                    }
                    catch (StackerException ex) { text.AppendLine("Discussion unavailable: " + ex.Message); }
                }
                if (text.Length > 2_000_000)
                {
                    text.AppendLine("Preview limit reached. Inspect individual layers.");
                    break;
                }
            }
            if (_disposed || generation != _generation)
                return;
            await ShowDocumentAsync(new(DiffDocumentMode.FileEvolution, sections, "File evolution"), selected.Change.NewPath);
            if (_disposed || generation != _generation)
                return;
            Preview = text.ToString();
            Message = "File evolution on the selected stack path. Comments stay in their original PR.";
        });
    }
}
