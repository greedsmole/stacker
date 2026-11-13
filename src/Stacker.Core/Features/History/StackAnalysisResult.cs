using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Outcome of one analysis pass: the revision that was analysed plus every finding on its edges.</summary>
/// <param name="Graph">The analysed revision; its pinned versions are the inputs, not the newest ones.</param>
/// <param name="Issues">Findings in edge order. An empty list means only that the captured edges produced no finding.</param>
public sealed record StackAnalysisResult(GraphSnapshot Graph, IReadOnlyList<StackAnalysisIssue> Issues);
