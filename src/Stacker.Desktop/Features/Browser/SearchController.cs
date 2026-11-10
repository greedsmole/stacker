using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

/// <summary>Executes bounded searches across the currently loaded comparison.</summary>
/// <remarks>The controller owns cancellation and one shared time budget. Text search examines only added and removed lines,
/// skips binary and submodule files, caps results, and reports unavailable files without failing the entire search.</remarks>
internal sealed class SearchController(ISearchPresentation owner, WorkspaceRuntime runtime)
{
    /// <summary>Opens search in the requested scope and hides the Git log.</summary>
    /// <param name="scope">Supported presentation scope, such as Files or Changed lines.</param>
    public void OpenSearch(string scope)
    { owner.IsGitLogOpen = false; owner.SearchScope = scope; owner.IsSearchOpen = true; }

    /// <summary>Cancels active work and closes the search panel.</summary>
    public void CloseSearch() { runtime._searchLoad?.Cancel(); owner.IsSearchOpen = false; }

    /// <summary>Cancels active work, removes results and restores the initial status message.</summary>
    public void ResetSearch()
    { runtime._searchLoad?.Cancel(); owner.SearchResults.Clear(); owner.SearchStatus = "Search files by name or added/removed code across this comparison."; }

    /// <summary>Searches until cancellation, timeout or the result cap is reached.</summary>
    public async Task SearchAsync()
    {
        runtime._searchLoad?.Cancel();
        owner.SearchResults.Clear();
        var request = SearchRequest.Create(owner.SearchQuery, owner.SearchRegex);
        if (request.IsFailed) { owner.SearchStatus = request.Problem!.Message; return; }
        var sections = owner.Sections.ToArray(); var hits = new List<ChangeSearchHit>(); var skipped = 0; var truncated = false; var patternTimeouts = 0;
        if (sections.Length == 0)
        {
            owner.SearchStatus = "Select a stack or pull request before searching its changes.";
            return;
        }
        using var load = new CancellationTokenSource(); runtime._searchLoad = load;
        using var deadline = new CancellationTokenSource(SearchRequest.Budget);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(load.Token, deadline.Token);
        // One budget for the whole search: a per-match timeout can still add up to minutes across a large stack.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var budget = SearchRequest.Budget; var stopped = false;
        owner.IsSearchBusy = true; owner.SearchStatus = "Searching…";
        try
        {
            bool Matches(string text) => request.Value!.Matches(text);
            foreach (var section in sections)
            {
                if (watch.Elapsed > budget) { stopped = true; break; }
                foreach (var file in section.Result.Files)
                {
                    load.Token.ThrowIfCancellationRequested();
                    if (watch.Elapsed > budget) { stopped = true; break; }
                    if (owner.SearchScope == "Files")
                    {
                        try
                        {
                            if (await Task.Run(() => Matches(file.DisplayPath), work.Token)) hits.Add(new(section, file, null));
                        }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !load.IsCancellationRequested) { stopped = true; }
                        catch (RegexMatchTimeoutException) { patternTimeouts++; }
                    }
                    else if (!file.IsBinary && !file.IsSubmodule)
                    {
                        try
                        {
                            var patch = (await runtime._git.PatchAsync(section.Root, section.Result.BaseSha, section.Result.HeadSha, file, work.Token)).OrThrow();
                            var found = await Task.Run(() =>
                            {
                                var matches = new List<ChangeSearchHit>();
                                foreach (var line in patch.Lines.Where(line => line.Kind is DiffLineKind.Added or DiffLineKind.Removed))
                                {
                                    work.Token.ThrowIfCancellationRequested();
                                    if (Matches(line.Text.Length > 0 ? line.Text[1..] : ""))
                                    { matches.Add(new(section, file, line)); if (hits.Count + matches.Count == SearchRequest.MaxResults) break; }
                                }
                                return matches;
                            }, work.Token);
                            hits.AddRange(found);
                        }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !load.IsCancellationRequested) { stopped = true; }
                        catch (RegexMatchTimeoutException) { patternTimeouts++; }
                        catch (StackerException) { skipped++; }
                    }
                    if (stopped) break;
                    if (hits.Count >= SearchRequest.MaxResults) { truncated = true; break; }
                }
                if (truncated || stopped) break;
            }
            load.Token.ThrowIfCancellationRequested();
            foreach (var hit in hits) owner.SearchResults.Add(hit);
            owner.SearchStatus = (hits.Count == 0 ? "No matches. Try a shorter query or change the search scope." : $"{hits.Count} result(s)") + (truncated ? " · first 500 shown" : "")
                + (stopped ? $" · stopped after {budget.TotalSeconds:0} s, narrow the search" : "")
                + (patternTimeouts > 0 ? $" · {patternTimeouts} file(s) where the pattern timed out" : "")
                + (skipped > 0 ? $" · {skipped} file(s) unavailable" : "");
        }
        catch (OperationCanceledException) { }
        catch (RegexMatchTimeoutException) { owner.SearchStatus = "Regex took too long. Try a narrower pattern."; }
        catch (Exception ex) { owner.SearchStatus = "Search failed: " + ex.Message; }
        finally { if (ReferenceEquals(runtime._searchLoad, load)) { runtime._searchLoad = null; owner.IsSearchBusy = false; } }
    }

    /// <summary>Expands and loads the section and file represented by a search result.</summary>
    /// <param name="hit">Result whose comparison location should become selected.</param>
    public async Task OpenSearchHitAsync(ChangeSearchHit hit)
    {
        if (!owner.Sections.Contains(hit.Section)) return;
        hit.Section.IsExpanded = true;
        if (!hit.Section.Files.Contains(hit.File)) hit.Section.Filter = "";
        hit.Section.SelectedFile = hit.File;
        await hit.Section.LoadAsync(hit.File);
    }

}
