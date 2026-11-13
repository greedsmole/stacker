using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>A stash commit recorded by the journal so it can be listed and restored by its owning entry.</summary>
/// <param name="Sha">Full commit id of the stash; the restore path finds it in the stash list by this id rather
/// than by a positional index.</param>
/// <param name="EntryId">Id of the journal entry that owns the stash, so unrelated restacks never overwrite it.</param>
/// <param name="CreatedAt">When the owning entry started; used for display.</param>
/// <param name="Description">The owning entry's kind, shown as the stash description.</param>
public sealed record SavedStash(string Sha, string EntryId, DateTimeOffset CreatedAt, string Description)
{
    /// <summary>A display label combining the created time, the description and a seven-character commit prefix;
    /// it is shown to the user and never parsed.</summary>
    /// <remarks>The seven characters are the conventional Git abbreviation and exist only to keep the label short.
    /// The stored <see cref="Sha"/> is always complete, and the prefix falls back to the whole value when the id is
    /// shorter than seven characters.</remarks>
    public string Label => $"{CreatedAt:g} · {Description} · {Sha[..Math.Min(7, Sha.Length)]}";
}
