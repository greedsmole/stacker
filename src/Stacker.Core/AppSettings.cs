namespace Stacker.Core;

/// <summary>Per-user preferences stored outside the repository, in the application-data folder.</summary>
/// <remarks>Loaded and saved as one document, with no per-property revision. Loading clamps the window and column sizes and fixes an unknown theme,
/// so a hand-edited or older document cannot put the shell into an unusable layout. A document whose <see cref="SchemaVersion"/> is newer than
/// <see cref="CurrentSchema"/> is preserved and writes stay blocked, rather than dropping fields this build does not know about.</remarks>
public sealed class AppSettings
{
    /// <summary>Bumped when a property is renamed or its meaning changes; unknown files are treated as defaults.</summary>
    /// <remarks>Schema 3 is current. Loading resets <see cref="SchemaVersion"/> to this value, so a stored document is read in place rather than migrated
    /// field by field: a property that is absent from the document keeps its default, and renaming or reinterpreting one is therefore what the bump records.
    /// A greater stored value blocks writes instead of being downgraded, because an older build would otherwise drop fields it cannot see.</remarks>
    public const int CurrentSchema = 3;
    /// <summary>The schema stamp read from the settings document and rewritten on every save.</summary>
    /// <remarks>Defaults to <see cref="CurrentSchema"/> so a fresh instance serializes at the current version. Only the comparison against
    /// <see cref="CurrentSchema"/> matters: greater blocks writes, smaller triggers no field-by-field migration.</remarks>
    public int SchemaVersion { get; set; } = CurrentSchema;
    /// <summary>Most recently opened local repository paths, ordered newest first by the UI.</summary>
    /// <remarks>The shell trims the list to ten when it records a repository, so the document cannot grow without bound. Entries are history
    /// suggestions, not guarantees: a path may no longer exist, and opening it then fails with an error instead of disappearing from the list.</remarks>
    public List<string> RecentRepositories { get; set; } = [];
    /// <summary>Enables GitHub metadata polling while the window is active; off means only an explicit refresh reads the remote.</summary>
    /// <remarks>On by default. The shell never polls for the offline demo or while no repository is open, and losing focus pauses the loop, so this
    /// setting alone cannot keep a background read running.</remarks>
    public bool BackgroundRefresh { get; set; } = true;
    /// <summary>Legacy profile field; repository objects are now managed by the selected Git repository.</summary>
    /// <remarks>Kept at its old default of 2048 MiB so an existing settings document round-trips unchanged. Nothing in the current read or write path
    /// consults it: disk usage belongs to Git, and the patch and syntax caches keep their own memory budgets, so changing it has no observable effect.
    /// Why 2048 MiB was chosen is not recorded.</remarks>
    public int CacheBudgetMiB { get; set; } = 2048;
    /// <summary>Persisted "wrap code" preference for diff panes; wrapping is applied to each section as it loads.</summary>
    /// <remarks>Changing it is immediate for open sections and is saved with the other preferences. It affects presentation only.</remarks>
    public bool WrapCode
    {
        get; set;
    }
    /// <summary>Displays changed files grouped by their Git paths.</summary>
    public bool GroupFilesByFolder
    {
        get; set;
    }
    public const int MinDiffLineHeight = 16;
    public const int MaxDiffLineHeight = 26;
    public const int DefaultDiffLineHeight = 18;
    public int DiffLineHeight { get; set; } = DefaultDiffLineHeight;
    /// <summary>Root of the repository that was last opened, so the shell can reopen it at launch.</summary>
    /// <remarks>Null before the first successful open. A path given on the command line wins over this value; a remembered path that has disappeared is
    /// reported as an error rather than silently dropped.</remarks>
    public string? LastRepository
    {
        get; set;
    }
    /// <summary>Explicit path to the Git executable; null asks for Git to be found on PATH and in the usual installation folders.</summary>
    /// <remarks>Needed when a Finder or Start-menu launch cannot see a non-system installation. Saving hands the new path to the shared executable
    /// resolver, so the next command uses it without a restart.</remarks>
    public string? GitPath
    {
        get; set;
    }
    /// <summary>Explicit path to the GitHub CLI executable; null asks for gh to be found on PATH and in the usual installation folders.</summary>
    /// <remarks>Applies to the API calls Stacker makes; Git keeps using its own transport and credentials.</remarks>
    public string? GhPath
    {
        get; set;
    }
    /// <summary>Editor used for conflicts and commit messages; empty means git core.editor, then the environment, then a platform default.</summary>
    /// <remarks>Stored as typed and split by <see cref="CommandLine"/> into a program plus arguments, then handed to <see cref="IEditorLauncher"/>, which
    /// trims it and prefers it over Git's own configuration. It is never run through a shell.</remarks>
    public string? EditorCommand
    {
        get; set;
    }
    /// <summary>Uses gh's saved account instead of token variables from the environment for GitHub API calls.</summary>
    /// <remarks>Off by default so an explicit <c>GH_TOKEN</c> or <c>GITHUB_TOKEN</c> keeps its normal precedence. When on, <c>GH_TOKEN</c>,
    /// <c>GITHUB_TOKEN</c>, <c>GH_ENTERPRISE_TOKEN</c> and <c>GITHUB_ENTERPRISE_TOKEN</c> are removed from the environment of gh API processes only; shell
    /// variables, Git credentials and gh's global account are not changed.</remarks>
    public bool UseSavedGhCredentials
    {
        get; set;
    }
    /// <summary>Shell theme name, <c>Dark</c> or <c>Light</c>; any other stored value is reset to <c>Dark</c> on load.</summary>
    /// <remarks>The theme is part of the diff view cache key, so switching it rebuilds highlighted sections instead of reusing tokens from the other palette.</remarks>
    public string Theme { get; set; } = "Dark";
    /// <summary>Width of the navigation column in logical pixels; default 270.</summary>
    /// <remarks>Loading clamps the stored value to 200-500 and falls back to 270 when it is not a finite number, so a corrupt value cannot collapse or hide
    /// the column. The pane enforces the same limits while the user resizes it.</remarks>
    public double NavigationWidth { get; set; } = 270;
    public bool StacksHidden
    {
        get; set;
    }
    /// <summary>Width of the per-comparison file column in logical pixels; default 290.</summary>
    /// <remarks>Loading clamps the stored value to 200-600 and falls back to 290 when it is not finite. The pane saves the width the user actually dragged to,
    /// which is why the stored value is re-clamped on the next load.</remarks>
    public double FilesWidth { get; set; } = 290;
    /// <summary>Initial window width in logical pixels; default 1400.</summary>
    /// <remarks>Loading clamps the stored value to 1000-4000 and falls back to 1400 when it is not finite, so a window cannot be restored below the layout's
    /// minimum or wider than a practical desktop.</remarks>
    public double WindowWidth { get; set; } = 1400;
    /// <summary>Initial window height in logical pixels; default 880.</summary>
    /// <remarks>Loading clamps the stored value to 640-2500 and falls back to 880 when it is not finite.</remarks>
    public double WindowHeight { get; set; } = 880;
}
