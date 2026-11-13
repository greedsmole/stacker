using System.Text.Json.Serialization;
namespace Stacker.Core;

/// <summary>Presentation state of one comparison section, restored when the same view is shown again.</summary>
/// <param name="Layer">The layer the section shows, as the full ref reported in the comparison result; it is the key
/// that re-associates a stored section with a freshly compared one.</param>
/// <param name="File">Repository-relative path of the selected file, or <see langword="null"/> when the section had no
/// file selected.</param>
/// <param name="Filter">The file filter text that was typed in this section.</param>
/// <param name="X">Horizontal scroll offset of the diff list, in the control's device-independent units.</param>
/// <param name="Y">Vertical scroll offset of the diff list, in the control's device-independent units.</param>
/// <param name="Expanded">Whether the section was expanded.</param>
public sealed record SectionViewState(string Layer, string? File, string Filter, double X, double Y, bool Expanded);
