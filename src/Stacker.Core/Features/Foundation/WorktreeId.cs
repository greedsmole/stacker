using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Persistent identity of one physical working tree inside a repository.</summary>
/// <param name="Value">Opaque identifier, 32 characters, generated when the location is first registered.</param>
/// <remarks>Linked worktrees of one repository share a <see cref="RepositoryId"/> but not a <see cref="WorktreeId"/>.
/// The workspace key is built from this value, so per-worktree view state follows the working tree instead of the
/// folder path and survives a move.</remarks>
public readonly record struct WorktreeId(string Value);
