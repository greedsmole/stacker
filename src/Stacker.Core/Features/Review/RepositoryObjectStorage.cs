using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Where downloaded pull request objects live and how much space they occupy.</summary>
/// <param name="CommonDirectory">The repository's shared Git directory, shown so the user knows which checkout stores the objects.</param>
/// <param name="SizeBytes">Loose, packed and garbage object bytes reported by Git, in bytes, for display only. The application
/// never deletes objects to reduce it; Git owns that storage.</param>
public sealed record RepositoryObjectStorage(string CommonDirectory, long SizeBytes);
