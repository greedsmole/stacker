using System.Text.RegularExpressions;
namespace Stacker.Core.Framework;

/// <summary>A local branch reference: the only thing Stacker is ever allowed to rewrite.</summary>
/// <remarks>Rewriting a ref outside <c>refs/heads/</c> would move something Stacker does not own and cannot safely
/// restore, so the parser requires the exact namespace, a non-empty short name, no control characters and no spaces.
/// The check is textual because Core must not run Git; full <c>check-ref-format</c> validation happens when the Git
/// boundary creates a branch. The character guard rejects names Git itself forbids here, with a clear message,
/// instead of letting an update fail later.</remarks>
public readonly record struct LocalBranch
{
    /// <summary>The exact namespace for local branches; comparison is ordinal, so a differently cased prefix is
    /// rejected.</summary>
    public const string Prefix = "refs/heads/";
    /// <summary>The full reference, used whenever the branch is passed to Git.</summary>
    public string Ref
    {
        get;
    }
    /// <summary>The short branch name after the prefix, used in messages and the UI.</summary>
    public string Name
    {
        get;
    }
    /// <summary>Splits the validated reference into its full and short forms; the constructor assumes the prefix is
    /// already present.</summary>
    /// <param name="reference">A validated full local-branch reference.</param>
    private LocalBranch(string reference)
    {
        Ref = reference;
        Name = reference[Prefix.Length..];
    }
    /// <summary>Validates a full local-branch reference.</summary>
    /// <remarks>A reference outside <see cref="Prefix"/> is refused with
    /// <see cref="ProblemCodes.GitNotLocalBranch"/> and a message that names the offending short form, because the
    /// user's next step is to point the layer at its local branch.</remarks>
    /// <param name="reference">The candidate full reference.</param>
    /// <returns>The value, or a problem explaining that Stacker cannot rewrite it.</returns>
    public static Result<LocalBranch> Parse(string? reference) =>
        reference is not null && reference.StartsWith(Prefix, StringComparison.Ordinal) && reference.Length > Prefix.Length && !reference.Any(char.IsControl) && !reference.Contains(' ')
            ? Result<LocalBranch>.Ok(new LocalBranch(reference))
            : Result<LocalBranch>.Fail(ProblemCodes.GitNotLocalBranch, $"{StackRefs.Short(reference ?? "")} is not a local branch, so Stacker cannot rewrite it.");
    /// <summary>Returns the short branch name.</summary>
    /// <returns>The name without its namespace.</returns>
    public override string ToString() => Name;
}
