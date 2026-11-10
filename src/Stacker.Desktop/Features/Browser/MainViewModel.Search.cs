// Purpose: Search commands for filenames and textual changes, with navigation back to the matching diff line.
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public sealed record ChangeSearchHit(DiffSectionViewModel Section, FileChange File, DiffLine? Line)
{
    /// <summary>Repository-relative path shown for this hit.</summary>
    public string Path => File.DisplayPath;
    /// <summary>Compact preview of the matching file or changed line.</summary>
    public string Preview => Line is null ? Section.Title :
        $"{(Line.Kind == DiffLineKind.Added ? "+" : "−")}{Line.NewLineNumber ?? Line.OldLineNumber}  {(Line.Text.Length > 0 ? Line.Text[1..] : "").Trim()}";
}

public sealed partial class MainViewModel
{
    /// <summary>Search modes offered by the comparison view.</summary>
    public IReadOnlyList<string> SearchScopes { get; } = ["Files", "Changed lines"];
    [ObservableProperty] internal bool _isSearchOpen;
    [ObservableProperty] internal string _searchScope = "Files";
    [ObservableProperty] internal string _searchQuery = "";
    [ObservableProperty] internal bool _searchRegex;
    [ObservableProperty] internal bool _isSearchBusy;
    [ObservableProperty] internal string _searchStatus = "Search files by name or added/removed code across this comparison.";
    public ObservableCollection<ChangeSearchHit> SearchResults { get; } = [];
    /// <summary>Opens search in the requested scope (file names or visible textual changes).</summary>
    public void OpenSearch(string scope) => _searchController.OpenSearch(scope);
    /// <summary>Closes search and cancels active search work.</summary>
    public void CloseSearch() => _searchController.CloseSearch();
    /// <summary>Clears search input and results.</summary>
    public void ResetSearch() => _searchController.ResetSearch();
    /// <summary>Runs the current search request.</summary>
    [RelayCommand] public Task SearchAsync() => _searchController.SearchAsync();
    /// <summary>Navigates to a hit's comparison and file, then loads its patch before selecting a matching line.</summary>
    public Task OpenSearchHitAsync(ChangeSearchHit hit) => _searchController.OpenSearchHitAsync(hit);
}
