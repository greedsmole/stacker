using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Resolved identity of an opened repository and worktree, together with the paths Git resolved for them.</summary>
/// <param name="Repository">Repository identity, shared by all of its worktrees.</param>
/// <param name="Worktree">Identity of this physical working tree.</param>
/// <param name="Root">Absolute working-tree root Git reported for the opened folder.</param>
/// <param name="CommonDirectory">Absolute Git common directory; linked worktrees of one repository share it.</param>
/// <remarks>Registration resolves <c>Root</c> and <c>CommonDirectory</c> from Git rather than trusting the path the
/// user typed, so a folder inside the tree resolves to the repository whose identity owns it. Relocation reuses both
/// identifiers with a new root, which is why history and workspace state survive a move.</remarks>
public sealed record RepositoryIdentity(RepositoryId Repository, WorktreeId Worktree, string Root, string CommonDirectory)
{
    /// <summary>History scope for this repository: the <c>local:</c> prefix followed by the repository id.</summary>
    /// <remarks>Derived from <see cref="RepositoryId"/>, not from <see cref="Root"/>, so moving or renaming the folder
    /// keeps the scope and its stored history. All worktrees of one repository therefore share one history scope,
    /// while workspace state keys use <see cref="WorktreeId"/> instead.</remarks>
    public string Scope => "local:" + Repository.Value;
}
