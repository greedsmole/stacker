using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>A file's evolution through a captured stack, oldest step first.</summary>
/// <remarks>Produced by <see cref="FileEvolutionService.BuildAsync"/>, which walks the selected path backwards from the
/// newest layer and then reverses the result, so the final step is the newest state and the path the user is looking at.</remarks>
/// <param name="Steps">Steps in ancestry order; empty only when the caller passed an empty path list.</param>
public sealed record FileEvolution(IReadOnlyList<FileEvolutionStep> Steps);
