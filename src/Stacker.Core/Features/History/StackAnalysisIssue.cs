using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One finding about one graph edge, attributed to the child that would have to move to resolve it.</summary>
/// <remarks><see cref="Kind"/> is one of the literal kinds <see cref="StackAnalysisService"/> produces —
/// <c>Unavailable</c>, <c>StaleParent</c>, <c>Overlap</c> or <c>Conflict</c> — and several findings may exist for the same
/// edge. Findings describe ancestry and text only: a clean analysis does not prove that a restack will apply or that the
/// code is correct.</remarks>
/// <param name="ChangeId">Child node of the offending edge, that is, the layer that would need attention.</param>
/// <param name="Kind">Finding kind; the closed set of values is listed in the remarks.</param>
/// <param name="Message">User-facing sentence explaining the finding.</param>
/// <param name="Paths">Common paths for <c>Overlap</c> and conflicting paths for <c>Conflict</c>; empty when the finding
/// concerns ancestry or missing evidence.</param>
public sealed record StackAnalysisIssue(string ChangeId, string Kind, string Message, IReadOnlyList<string> Paths);
