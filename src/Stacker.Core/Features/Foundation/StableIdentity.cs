using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Derives stable, content-addressed identifiers from the values that define a thing.</summary>
/// <remarks>Used for change nodes, observed versions, comparison identities and cache keys. Stored history, checkpoints
/// and drafts are looked up by these ids, so the same inputs must produce the same value on every run and every
/// machine; a random or time-dependent value would reopen stored data under a new identity.</remarks>
public static class StableIdentity
{
    /// <summary>Returns the lowercase hexadecimal SHA-256 digest of the values joined by a NUL separator.</summary>
    /// <param name="values">Fields that identify the thing, in a fixed order; changing the order changes the id.</param>
    /// <returns>64 lowercase hexadecimal characters: the full 32-byte SHA-256 digest, untruncated and unsalted.</returns>
    /// <remarks>Fields are joined with U+0000 before UTF-8 encoding. NUL cannot occur in a Git ref or a path, so two
    /// field lists cannot be made to produce the same joined text by shifting characters across a field boundary.
    /// The digest is not keyed: equal inputs anywhere in the process tree produce equal ids, and an empty list hashes
    /// the empty string.</remarks>
    public static string For(params string[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', values)))).ToLowerInvariant();
}
