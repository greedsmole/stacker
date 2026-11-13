using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Reviewer's marker that one path was reviewed at one concrete version.</summary>
/// <remarks>Stored per change and path, newest marker winning, so re-marking replaces instead of stacking. The recorded
/// <see cref="VersionId"/> is what makes progress classification possible: the later comparison starts from that version.
/// If the version is no longer stored — for example because its history was removed — the marker cannot be compared and the
/// path reads as unreviewed again.</remarks>
/// <param name="ChangeId">Change the marker belongs to.</param>
/// <param name="Path">Reviewed path on the new side; a renamed file is keyed by its current name.</param>
/// <param name="VersionId">Version that was on screen when the marker was set.</param>
/// <param name="ReviewedAt">When the marker was set; the newest one per path is the effective one.</param>
public sealed record ReviewCheckpoint(string ChangeId, string Path, string VersionId, DateTimeOffset ReviewedAt);
