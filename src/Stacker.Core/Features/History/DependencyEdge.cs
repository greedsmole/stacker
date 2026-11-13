using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Directed relation stating that the child change is stacked on the parent change, with its provenance.</summary>
/// <remarks><see cref="Source"/> records how the edge was established: <c>local-definition</c> from the declared stack
/// order, <c>github</c> when the base ref is another pull request's head ref in the repository this change targets, and
/// <c>explicit</c> from the dependency editor. <see cref="ChangeGraphService"/> keeps explicit edges across observations
/// and lets them win over an inferred edge for the same child. Several edges may point at one child; only children with
/// exactly one parent are navigable as stack paths.</remarks>
/// <param name="Parent">Change that provides the base and therefore has to land first.</param>
/// <param name="Child">Change stacked on <see cref="Parent"/>.</param>
/// <param name="Source">Edge provenance: <c>local-definition</c>, <c>github</c> or <c>explicit</c>.</param>
public sealed record DependencyEdge(string Parent, string Child, string Source);
