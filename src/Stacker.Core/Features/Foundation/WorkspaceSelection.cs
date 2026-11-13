namespace Stacker.Core;

/// <summary>Navigation state recorded by the workspace: the change, the comparison and the file in view.</summary>
/// <param name="ChangeId">Id of the change node being reviewed, or <see langword="null"/> when nothing is selected.</param>
/// <param name="ComparisonId">Comparison identity of the displayed comparison, or <see langword="null"/>.</param>
/// <param name="Path">Path of the selected file inside that comparison, or <see langword="null"/>.</param>
/// <remarks>Value equality is significant: the session ignores a selection equal to the current one, so reselecting the
/// same target must not invalidate in-flight queries. The session stores this record as its selection state and compares
/// whole values; it is not a ticket and does not by itself prove that a background result is current.</remarks>
public sealed record WorkspaceSelection(string? ChangeId, string? ComparisonId, string? Path);
