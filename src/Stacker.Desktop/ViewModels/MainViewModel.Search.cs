// Purpose: Search commands for filenames and textual changes, with navigation back to the matching diff line.
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public sealed record ChangeSearchHit(DiffSectionViewModel Section, FileChange File, DiffLine? Line)
{
    public string Path => File.DisplayPath;
    public string Preview => Line is null ? Section.Title :
        $"{(Line.Kind == DiffLineKind.Added ? "+" : "−")}{Line.NewLineNumber ?? Line.OldLineNumber}  {(Line.Text.Length > 0 ? Line.Text[1..] : "").Trim()}";
}

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _searchLoad;
    public IReadOnlyList<string> SearchScopes { get; } = ["Files", "Changed lines"];
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _searchScope = "Files";
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _searchRegex;
    [ObservableProperty] private bool _isSearchBusy;
    [ObservableProperty] private string _searchStatus = "Search files by name or added/removed code across this comparison.";
    public ObservableCollection<ChangeSearchHit> SearchResults { get; } = [];
    /// <summary>Opens search in the requested scope (file names or visible textual changes).</summary>
    public void OpenSearch(string scope)
    { IsGitLogOpen = false; SearchScope = scope; IsSearchOpen = true; }
    public void CloseSearch() { _searchLoad?.Cancel(); IsSearchOpen = false; }
    private void ResetSearch()
    { _searchLoad?.Cancel(); SearchResults.Clear(); SearchStatus = "Search files by name or added/removed code across this comparison."; }
    [RelayCommand] public async Task SearchAsync()
    {
        _searchLoad?.Cancel();
        SearchResults.Clear();
        var query = SearchQuery.Trim();
        if (query.Length == 0) { SearchStatus = "Enter a file name or text to search."; return; }
        Regex? pattern = null;
        try
        {
            if (SearchRegex) pattern = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException ex) { SearchStatus = "Invalid regex: " + ex.Message; return; }
        using var load = new CancellationTokenSource(); _searchLoad = load;
        var sections = Sections.ToArray(); var hits = new List<ChangeSearchHit>(); var skipped = 0; var truncated = false;
        IsSearchBusy = true; SearchStatus = "Searching…";
        try
        {
            bool Matches(string text) => pattern?.IsMatch(text) ?? text.Contains(query, StringComparison.OrdinalIgnoreCase);
            foreach (var section in sections)
            {
                foreach (var file in section.Result.Files)
                {
                    load.Token.ThrowIfCancellationRequested();
                    if (SearchScope == "Files")
                    {
                        if (Matches(file.DisplayPath)) hits.Add(new(section, file, null));
                    }
                    else if (!file.IsBinary && !file.IsSubmodule)
                    {
                        try
                        {
                            var patch = await _git.PatchAsync(section.Root, section.Result.BaseSha, section.Result.HeadSha, file, load.Token);
                            foreach (var line in patch.Lines.Where(line => line.Kind is DiffLineKind.Added or DiffLineKind.Removed))
                                if (Matches(line.Text.Length > 0 ? line.Text[1..] : ""))
                                { hits.Add(new(section, file, line)); if (hits.Count == 500) break; }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (RegexMatchTimeoutException) { skipped++; }
                        catch (StackerException) { skipped++; }
                    }
                    if (hits.Count >= 500) { truncated = true; break; }
                }
                if (truncated) break;
            }
            load.Token.ThrowIfCancellationRequested();
            foreach (var hit in hits) SearchResults.Add(hit);
            SearchStatus = $"{hits.Count} result(s)" + (truncated ? " · first 500 shown" : "") + (skipped > 0 ? $" · {skipped} file(s) unavailable" : "");
        }
        catch (OperationCanceledException) { }
        catch (RegexMatchTimeoutException) { SearchStatus = "Regex took too long. Try a narrower pattern."; }
        catch (Exception ex) { SearchStatus = "Search failed: " + ex.Message; }
        finally { if (ReferenceEquals(_searchLoad, load)) { _searchLoad = null; IsSearchBusy = false; } }
    }
    /// <summary>Navigates to a hit's comparison and file, then loads its patch before selecting a matching line.</summary>
    public async Task OpenSearchHitAsync(ChangeSearchHit hit)
    {
        if (!Sections.Contains(hit.Section)) return;
        hit.Section.IsExpanded = true;
        if (!hit.Section.Files.Contains(hit.File)) hit.Section.Filter = "";
        hit.Section.SelectedFile = hit.File;
        await hit.Section.LoadAsync(hit.File);
    }
}
