using System.Text.RegularExpressions;
namespace Stacker.Core.Framework;

/// <summary>A repository root exactly as Git reports it, so every store key is stable.</summary>
/// <remarks>The value is the literal top-level path Git reports, with no path normalization. Trimming, resolving
/// symlinks or changing separators here would give one checkout two identities and split its journal, locks and
/// settings. The parser only rejects blank input because Core must not touch the filesystem; whether the directory
/// exists and is a repository is decided by the Git adapter before this value is created.</remarks>
public readonly record struct RepoRoot
{
    /// <summary>The root path, exactly as the Git adapter reported it.</summary>
    public string Value
    {
        get;
    }
    /// <summary>Stores an already validated root; the public entry points are <see cref="Parse"/> and
    /// <see cref="From"/>.</summary>
    /// <param name="value">The validated root path.</param>
    private RepoRoot(string value) => Value = value;
    /// <summary>Creates a root from an already canonical path and throws when it is blank.</summary>
    /// <remarks>For call sites that hold a canonical root and treat a missing one as a programming error. The public
    /// parser is the fallible form.</remarks>
    /// <param name="canonical">The canonical root path.</param>
    /// <returns>The root value.</returns>
    /// <exception cref="StackerException">The path is null, empty or whitespace.</exception>
    public static RepoRoot From(string canonical) => Parse(canonical).OrThrow();
    /// <summary>Validates that a root was supplied.</summary>
    /// <remarks>A blank root is refused because every repository would otherwise share one empty store key, making
    /// their journals and locks collide. Path existence is deliberately not checked here.</remarks>
    /// <param name="path">The candidate root path.</param>
    /// <returns>The root value, or <see cref="ProblemCodes.StateInvalid"/> when the path is blank.</returns>
    public static Result<RepoRoot> Parse(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Result<RepoRoot>.Fail(ProblemCodes.StateInvalid, "A repository root is required.") : Result<RepoRoot>.Ok(new RepoRoot(path));
    /// <summary>Returns the root path so logs and messages show the store identity.</summary>
    /// <returns>The full root path.</returns>
    public override string ToString() => Value;
}
