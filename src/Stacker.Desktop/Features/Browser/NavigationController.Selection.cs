using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class NavigationController
{
    public void SetActiveGroup(StackGroupViewModel group)
    {
        runtime.Session.Select(new(group.Definition.Id, null, null));
        runtime._comparison?.Cancel();
        foreach (var g in owner.LocalGroups.Concat(owner.RemoteGroups).Concat(owner.OtherGroups))
        {
            g.IsSelected = ReferenceEquals(g, group);
            foreach (var l in g.Layers)
            {
                l.IsSelected = false;
                l.IsChecked = false;
            }
        }
        runtime._activeGroup = group;
        runtime._suppress = true;
        owner.SelectedStack = group.Definition;
        runtime._suppress = false;
        owner.Layers.Clear();
        foreach (var layer in group.Layers)
            owner.Layers.Add(layer);
        owner.Notify(nameof(owner.BaseLabel));
        owner.Notify(nameof(owner.IsLocalSelected));
        owner.Notify(nameof(owner.CanRestackActiveStack));
    }

    public async Task SelectGroupAsync(StackGroupViewModel group, bool saveCurrent = true)
    {
        if (saveCurrent)
            await owner.SaveWorkspaceAsync();
        if (group.IsSinglePr)
        {
            SetActiveGroup(group);
            runtime._selectedPositions = [0];
            runtime._suppress = true;
            owner.Mode = DiffMode.Layer;
            runtime._suppress = false;
            await CompareAsync();
            return;
        }
        SetActiveGroup(group);
        runtime._selectedPositions = [];
        runtime._suppress = true;
        owner.Mode = DiffMode.FullStack;
        if (runtime._workspace.Stacks.TryGetValue(owner.GroupKey(group), out var saved))
        {
            runtime._selectedPositions = group.Layers.Where(l => saved.Branches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray();
            if (saved.Branches.Length == runtime._selectedPositions.Length)
                owner.Mode = saved.Mode;
            else
            {
                runtime._selectedPositions = [];
                owner.EmptyMessage = "The saved layer is no longer available. Showing Overview.";
            }
        }
        runtime._suppress = false;
        await CompareAsync();
    }

    public async Task SelectLayerAsync(LayerViewModel layer, bool toggle, CancellationToken ct = default)
    {
        await owner.SaveWorkspaceAsync();
        ct.ThrowIfCancellationRequested();
        var group = layer.Group!;
        if (!ReferenceEquals(runtime._activeGroup, group))
        {
            SetActiveGroup(group);
            runtime._selectedPositions = [];
        }
        runtime._selectedPositions = toggle ? (runtime._selectedPositions.Contains(layer.Snapshot.Position) ? runtime._selectedPositions.Where(p => p != layer.Snapshot.Position) : runtime._selectedPositions.Append(layer.Snapshot.Position)).Order().ToArray() : [layer.Snapshot.Position];
        runtime._suppress = true;
        owner.Mode = runtime._selectedPositions.Length == 0 ? DiffMode.FullStack : runtime._selectedPositions.Length == 1 ? DiffMode.Layer : DiffMode.MultiLayer;
        runtime._suppress = false;
        await CompareAsync(ct);
    }

    public void SelectLayers(IEnumerable<LayerViewModel> layers)
    {
        runtime._selectedPositions = layers.Select(l => l.Snapshot.Position).Order().ToArray();
        runtime._suppress = true;
        owner.Mode = runtime._selectedPositions.Length > 1 ? DiffMode.MultiLayer : DiffMode.Layer;
        runtime._suppress = false;
        _ = CompareAsync();
    }

    public async Task CompareAsync(CancellationToken ct = default)
    {
        owner.ResetSearch();
        runtime._comparison?.Cancel();
        var compare = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runtime._comparison = compare;
        var group = runtime._activeGroup;
        var repo = runtime._repository;
        var mode = owner.Mode;
        var positions = runtime._selectedPositions.ToArray();
        if (repo is null || group is null || runtime._disposed)
        {
            compare.Dispose();
            if (ReferenceEquals(runtime._comparison, compare))
                runtime._comparison = null;
            return;
        }
        runtime.Session.Select(new(group.Definition.Id, mode + ":" + string.Join(",", positions), null));
        var ticket = runtime.Session.Ticket;
        owner.Notify(nameof(owner.HasPrDiscussion));
        owner.Notify(nameof(owner.CanReview));
        owner.IsBusy = true;
        owner.Error = "";
        try
        {
            var comparisonRepository = repo;
            var stack = group.Definition;
            if (group.IsRemote)
            {
                if (runtime._remoteSnapshot is null)
                    throw new StackerException("Refresh to load this stack.");
                comparisonRepository = await owner.PrepareRemoteAsync(group, mode, positions, compare.Token);
                stack = new(group.Definition.Id, group.Name, RemoteRefs.BaseRef(group.PullRequests[0]), group.PullRequests.Select(RemoteRefs.HeadRef).ToArray());
                // For a single PR's review use its declared server base, even if the discovered parent has moved.
                if (mode == DiffMode.Layer && positions.Length == 1)
                {
                    var pr = group.PullRequests[positions[0]];
                    stack = new(stack.Id, stack.Name, RemoteRefs.BaseRef(pr), [RemoteRefs.HeadRef(pr)]);
                    positions = [0];
                }
            }
            var key = WorkspaceRuntime.ComparisonKey(comparisonRepository, stack, mode, positions) + "|" + owner.Settings.Theme + (group.IsRemote ? "|" + runtime._remoteSnapshot?.Context.Identity : "");
            if (!runtime._views.TryGetValue(key, out var sections))
            {
                var results = await runtime._diff.CompareAsync(new(comparisonRepository, stack, mode, positions), compare.Token);
                compare.Token.ThrowIfCancellationRequested();
                sections = results.Select(result =>
                {
                    var pr = group.IsRemote ? group.PullRequests.FirstOrDefault(p => RemoteRefs.HeadRef(p) == result.Layer) : owner.FindLocalPr(result.Layer);
                    return new DiffSectionViewModel(runtime._git, comparisonRepository.Root, result, runtime._highlighter, owner.Settings.Theme)
                    {
                        AssociatedPr = pr,
                        GitHubContext = runtime._remoteSnapshot?.Context,
                        IsPrSnapshot = group.IsRemote && mode == DiffMode.Layer,
                        IsDemo = owner.IsDemo,
                        WrapCode = owner.Settings.WrapCode,
                        GroupFilesByFolder = owner.Settings.GroupFilesByFolder
                    };
                }).ToList();
                if (group.IsRemote && runtime._prObjects is not null && !owner.IsDemo)
                    foreach (var section in sections)
                        section.ContentLease = (await runtime._prObjects.AcquireAsync(repo.Identity!, compare.Token)).OrThrow();
                // Carry file/filter/scroll state across a changed snapshot of the same comparison.
                foreach (var section in sections)
                {
                    var old = owner.Sections.FirstOrDefault(s => s.Result.Layer == section.Result.Layer);
                    if (old is not null)
                        section.RestoreView(old);
                }
                runtime._views[key] = sections;
                while (runtime._views.Count > 24)
                {
                    var victim = runtime._views.First(kv => kv.Key != key && !kv.Value.Any(owner.Sections.Contains));
                    foreach (var s in victim.Value)
                        s.Dispose();
                    runtime._views.Remove(victim.Key);
                }
            }
            compare.Token.ThrowIfCancellationRequested();
            if (!runtime.Session.IsCurrent(ticket))
                return;
            if (!owner.Sections.SequenceEqual(sections))
            {
                owner.Sections.Clear();
                foreach (var section in sections)
                    owner.Sections.Add(section);
            }
            foreach (var layer in group.Layers)
            {
                layer.IsChecked = runtime._selectedPositions.Contains(layer.Snapshot.Position);
                layer.IsSelected = layer.IsChecked;
            }
            owner.ComparisonTitle = mode switch
            {
                DiffMode.FullStack => $"Entire stack · {group.Name}",
                DiffMode.Cumulative => $"Through layer {(runtime._selectedPositions.FirstOrDefault() + 1)} · {group.Name}",
                DiffMode.MultiLayer => $"Selected layers · {string.Join(", ", runtime._selectedPositions.Select(p => p + 1))}",
                _ => group.IsSinglePr ? $"Pull request · {group.PullRequests[0].Label}" : $"Layer {(runtime._selectedPositions.FirstOrDefault() + 1)} · {group.Layers.ElementAtOrDefault(runtime._selectedPositions.FirstOrDefault())?.Title}"
            };
            owner.Notify(nameof(owner.ComparisonDetail));
            owner.RestoreSections();
            foreach (var section in sections)
                section.ShowSectionHeader = sections.Count > 1;
            if (mode == DiffMode.Layer)
                foreach (var section in sections)
                    await owner.CaptureHistorySectionAsync(section, compare.Token);
            var prSection = sections.FirstOrDefault(s => s.IsPrSnapshot);
            if (prSection is not null)
            {
                var review = await owner.EnsureReviewAsync(prSection, compare.Token);
                compare.Token.ThrowIfCancellationRequested();
                owner.ActiveReview = review;
            }
            else
            {
                owner.ActiveReview = null;
                owner.IsReviewOpen = false;
            }
            compare.Token.ThrowIfCancellationRequested();
            owner.Notify(nameof(owner.HasSelectedLayer));
            owner.EmptyMessage = sections.Count == 0 ? "This stack has no layers. Edit it to add branches." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!compare.IsCancellationRequested) owner.Error = ex.Message; }
        finally { if (ReferenceEquals(runtime._comparison, compare)) { runtime._comparison = null; owner.IsBusy = false; } compare.Dispose(); }
    }
}
