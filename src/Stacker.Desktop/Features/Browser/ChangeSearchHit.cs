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
