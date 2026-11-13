using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Persistent identity of a repository, shared by every worktree that resolves the same Git common directory.</summary>
/// <param name="Value">Opaque identifier. A registered repository stores a 32-character GUID; identities derived by a
/// reader use the first 32 characters of a <see cref="StableIdentity"/> digest.</param>
/// <remarks>Value equality only — the string is compared verbatim, so an identifier read from storage must be handed
/// back unchanged. The identity, not the folder path, keys history, drafts and workspace state; that is what lets a
/// moved repository keep its past and keeps two unrelated clones from merging. There is no parsing or normalisation,
/// so an empty or malformed value is accepted here and rejected only where it is looked up.</remarks>
public readonly record struct RepositoryId(string Value);
