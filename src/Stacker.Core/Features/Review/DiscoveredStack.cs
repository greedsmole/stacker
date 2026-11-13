using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One discovered chain of pull requests from the branch nearest the base upward.</summary>
/// <param name="Id">Stable identifier of the chain, derived from its topmost pull request number.</param>
/// <param name="Name">Display name of the chain, taken from the topmost pull request title.</param>
/// <param name="Layers">Pull requests ordered base to head; each one's base ref is the previous one's head ref.</param>
/// <param name="SharedPrefix">True when at least one pull request also appears in another discovered chain, meaning the
/// chains fork from a common bottom layer.</param>
public sealed record DiscoveredStack(string Id, string Name, IReadOnlyList<PullRequest> Layers, bool SharedPrefix);
