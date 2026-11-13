using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Result of grouping a snapshot into pull request chains, with the leftovers and the refusals.</summary>
/// <param name="Stacks">Chains of at least two pull requests, ordered as discovered.</param>
/// <param name="Other">Open pull requests that belong to no chain, shown separately instead of being forced into one.</param>
/// <param name="Warnings">Duplicate-parent, cycle and blocked-edge messages explaining why a pull request is absent.</param>
public sealed record DiscoveryResult(IReadOnlyList<DiscoveredStack> Stacks, IReadOnlyList<PullRequest> Other, IReadOnlyList<string> Warnings);
