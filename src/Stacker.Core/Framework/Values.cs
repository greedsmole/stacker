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
    public string Value { get; }
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

/// <summary>A complete commit id; short ids and revisions are rejected.</summary>
/// <remarks>The pattern accepts exactly 40 lowercase hexadecimal characters (SHA-1) or 64 (SHA-256). Abbreviated ids
/// and revision expressions are refused on purpose: an abbreviation can become ambiguous as objects are added, and a
/// symbolic name such as a branch can resolve to a different commit later, so neither is a stable identity for a
/// journal entry. Uppercase is refused because Git reports lowercase ids and comparisons are ordinal. No abbreviation
/// is accepted or stored: the only shortening in this layer is the display-only seven-character label
/// (<see cref="SavedStash.Label"/>) and the removal of a remote name from
/// <see cref="RemoteTrackingRef.BranchName"/>; both are presentation, never identity. The pattern is anchored with
/// <c>\A</c> and <c>\z</c> rather than caret and dollar, so a trailing newline cannot satisfy it.</remarks>
public readonly partial record struct CommitSha
{
    /// <summary>The complete, lowercase commit id.</summary>
    public string Value { get; }
    /// <summary>Stores an already validated id; <see cref="Parse"/> is the public entry point.</summary>
    /// <param name="value">The validated commit id.</param>
    private CommitSha(string value) => Value = value;
    /// <summary>The generated whole-string pattern for a SHA-1 or SHA-256 id.</summary>
    /// <remarks>The alternation is wrapped in a non-capturing group so both alternatives are anchored. Keeping the
    /// pattern private means every caller goes through <see cref="IsValid"/> or <see cref="Parse"/> and cannot
    /// accidentally match a substring.</remarks>
    /// <returns>The compiled regular expression.</returns>
    [GeneratedRegex("\\A(?:[0-9a-f]{40}|[0-9a-f]{64})\\z")]
    private static partial Regex Pattern();
    /// <summary>Checks a complete commit id without creating a value; null is invalid.</summary>
    /// <param name="value">The candidate text.</param>
    /// <returns>True only for a complete 40- or 64-character lowercase hexadecimal id.</returns>
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);
    /// <summary>Creates a commit id, or reports that the text is not a complete id.</summary>
    /// <remarks>This is the guard used wherever an id comes from text (a stash reference, a commit id returned by an
    /// effect), so an abbreviation or a symbolic name is not stored as if it identified one immutable commit.</remarks>
    /// <param name="value">The candidate text.</param>
    /// <returns>The value, or <see cref="ProblemCodes.StateInvalid"/> with a display-ready message.</returns>
    public static Result<CommitSha> Parse(string? value) =>
        IsValid(value) ? Result<CommitSha>.Ok(new CommitSha(value!)) : Result<CommitSha>.Fail(ProblemCodes.StateInvalid, "Invalid commit id.");
    /// <summary>Returns the full commit id.</summary>
    /// <returns>The complete id.</returns>
    public override string ToString() => Value;
}

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
    public string Ref { get; }
    /// <summary>The short branch name after the prefix, used in messages and the UI.</summary>
    public string Name { get; }
    /// <summary>Splits the validated reference into its full and short forms; the constructor assumes the prefix is
    /// already present.</summary>
    /// <param name="reference">A validated full local-branch reference.</param>
    private LocalBranch(string reference) { Ref = reference; Name = reference[Prefix.Length..]; }
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

/// <summary>A remote-tracking reference, which can be compared against but never rewritten.</summary>
/// <remarks>The value only has to carry the <see cref="Prefix"/> namespace and a non-empty short name. The stricter
/// character guard used for local branches is not repeated because this value is never passed back as a rewrite
/// target; it is read from Git's ref listing and used for comparison and naming.</remarks>
public readonly record struct RemoteTrackingRef
{
    /// <summary>The exact namespace for remote-tracking refs; comparison is ordinal.</summary>
    public const string Prefix = "refs/remotes/";
    /// <summary>The full reference, used when reading the tracked commit.</summary>
    public string Ref { get; }
    /// <summary>Everything after the prefix: the remote name plus the branch name.</summary>
    public string ShortName { get; }
    /// <summary>Splits the validated reference; the constructor assumes the prefix is already present.</summary>
    /// <param name="reference">A validated remote-tracking reference.</param>
    private RemoteTrackingRef(string reference) { Ref = reference; ShortName = reference[Prefix.Length..]; }
    /// <summary>Validates that the reference is a remote-tracking ref.</summary>
    /// <param name="reference">The candidate full reference.</param>
    /// <returns>The value, or <see cref="ProblemCodes.StateInvalid"/> when the namespace or name is wrong.</returns>
    public static Result<RemoteTrackingRef> Parse(string? reference) =>
        reference is not null && reference.StartsWith(Prefix, StringComparison.Ordinal) && reference.Length > Prefix.Length
            ? Result<RemoteTrackingRef>.Ok(new RemoteTrackingRef(reference))
            : Result<RemoteTrackingRef>.Fail(ProblemCodes.StateInvalid, "Not a remote-tracking reference.");
    /// <summary>The branch name a GitHub pull request would use for this ref ("origin/main" becomes "main").</summary>
    /// <remarks>Only the first segment, the remote name, is dropped; everything after the first slash is kept, so a
    /// branch that itself contains slashes survives. A short name without a slash is returned unchanged.</remarks>
    public string BranchName => ShortName.Contains('/') ? ShortName[(ShortName.IndexOf('/') + 1)..] : ShortName;
    /// <summary>Returns the short form, including the remote name.</summary>
    /// <returns>The reference without its namespace.</returns>
    public override string ToString() => ShortName;
}

/// <summary>A stack layer ref, tagged with whether Stacker may rewrite it.</summary>
/// <remarks>Exactly one side is set by the private constructor. Parsing tries the local namespace first, and the
/// properties that callers use for display and Git calls dereference the non-null side. <see cref="RequireLocal"/>
/// turns a read-only layer into an actionable refusal instead of letting a caller treat it as writable.</remarks>
public readonly record struct StackBranchRef
{
    /// <summary>The local side when this value is rewritable; <see langword="null"/> for a remote-tracked ref.</summary>
    public LocalBranch? Local { get; }
    /// <summary>The remote-tracking side when this value is read-only; <see langword="null"/> for a local ref.</summary>
    public RemoteTrackingRef? Remote { get; }
    /// <summary>Stores the parsed side; callers obtain values through <see cref="Parse"/>, which sets exactly
    /// one.</summary>
    /// <param name="local">The local side, when present.</param>
    /// <param name="remote">The remote side, when present.</param>
    private StackBranchRef(LocalBranch? local, RemoteTrackingRef? remote) { Local = local; Remote = remote; }
    /// <summary>True when this ref may be rewritten locally.</summary>
    public bool IsLocal => Local is not null;
    /// <summary>The full ref of whichever side is set.</summary>
    public string Ref => Local?.Ref ?? Remote!.Value.Ref;
    /// <summary>The display name: the local short name, or the remote's branch name without the remote.</summary>
    public string Name => Local?.Name ?? Remote!.Value.BranchName;
    /// <summary>Parses a local branch first, then a remote-tracking reference.</summary>
    /// <remarks>A null reference and any other namespace both fail with <see cref="ProblemCodes.StateInvalid"/>; the
    /// unsupported case repeats the raw reference so the user can see what was persisted.</remarks>
    /// <param name="reference">The full reference to classify.</param>
    /// <returns>The classified ref, or a problem when it belongs to neither namespace.</returns>
    public static Result<StackBranchRef> Parse(string? reference)
    {
        if (reference is null) return Result<StackBranchRef>.Fail(ProblemCodes.StateInvalid, "A branch reference is required.");
        var local = LocalBranch.Parse(reference);
        if (local.IsOk) return Result<StackBranchRef>.Ok(new(local.Value, null));
        var remote = RemoteTrackingRef.Parse(reference);
        return remote.IsOk ? Result<StackBranchRef>.Ok(new(null, remote.Value)) : Result<StackBranchRef>.Fail(ProblemCodes.StateInvalid, $"Unsupported reference: {reference}");
    }
    /// <summary>Requires a rewritable layer.</summary>
    /// <remarks>The publication planner calls this before treating a layer as writable. A remote-tracked layer
    /// cannot be restacked without changing origin, so the failure names the local branch the user should select
    /// instead.</remarks>
    /// <returns>The local branch, or <see cref="ProblemCodes.GitNotLocalBranch"/> with guidance.</returns>
    public Result<LocalBranch> RequireLocal() => Local is { } branch
        ? Result<LocalBranch>.Ok(branch)
        : Result<LocalBranch>.Fail(ProblemCodes.GitNotLocalBranch, $"{Name} is tracked from a remote and cannot be restacked. Use your local branch of the same name.");
    /// <summary>Returns the display name.</summary>
    /// <returns>The short local name or the remote branch name.</returns>
    public override string ToString() => Name;
}

/// <summary>A commit message that is safe to pass to git as a single argument.</summary>
/// <remarks>The text is trimmed and must be non-empty, because Git would otherwise refuse the commit or record a
/// meaningless message. The 8000-character cap bounds what reaches the Git argument list and the UI; no such Git limit
/// is encoded here and the rationale for 8000 is not recorded. The process boundary passes each argument separately,
/// so quoting is not the reason for the cap, and newlines inside the message are preserved.</remarks>
public readonly record struct CommitMessage
{
    /// <summary>The trimmed message.</summary>
    public string Value { get; }
    /// <summary>Stores an already validated message; <see cref="Parse"/> is the public entry point.</summary>
    /// <param name="value">The trimmed message.</param>
    private CommitMessage(string value) => Value = value;
    /// <summary>Trims a user-authored message and rejects empty or oversized text.</summary>
    /// <param name="text">The raw text from the editor; may be null.</param>
    /// <returns>The message, or <see cref="ProblemCodes.StateInvalid"/> with a display-ready reason.</returns>
    public static Result<CommitMessage> Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return Result<CommitMessage>.Fail(ProblemCodes.StateInvalid, "Enter a commit message.");
        if (trimmed.Length > 8000) return Result<CommitMessage>.Fail(ProblemCodes.StateInvalid, "The commit message is too long.");
        return Result<CommitMessage>.Ok(new CommitMessage(trimmed));
    }
    /// <summary>Returns the trimmed message.</summary>
    /// <returns>The stored text.</returns>
    public override string ToString() => Value;
}

/// <summary>An editor or tool command, kept as a program plus arguments so no shell is ever involved.</summary>
/// <remarks>A user-authored command is split once into a program and arguments; Stacker launches it directly, so
/// spaces and metacharacters stay literal in an argument. Git is different: it evaluates the GIT_EDITOR value through
/// a shell, so <see cref="ForGitEditor"/> re-quotes each token. A blank command fails with
/// <see cref="ProblemCodes.EditorMissing"/> so Git never receives an empty editor value.</remarks>
public readonly record struct ShellCommand
{
    /// <summary>The executable to launch, exactly as the user wrote it (after quote removal).</summary>
    public string Program { get; }
    /// <summary>The arguments that follow the executable, each already separated.</summary>
    public IReadOnlyList<string> Arguments { get; }
    /// <summary>Stores an already split command; <see cref="Parse"/> is the public entry point.</summary>
    /// <param name="program">The executable.</param>
    /// <param name="arguments">The separated arguments.</param>
    private ShellCommand(string program, IReadOnlyList<string> arguments) { Program = program; Arguments = arguments; }
    /// <summary>Splits a configured editor command without invoking a shell.</summary>
    /// <remarks>Quoted paths with spaces stay one argument. A blank value, and a command the splitter rejects, both
    /// become <see cref="ProblemCodes.EditorMissing"/> with display-ready text, because there is no usable editor to
    /// launch.</remarks>
    /// <param name="command">The raw configured command.</param>
    /// <returns>The parsed command, or a problem when no editor can be launched.</returns>
    public static Result<ShellCommand> Parse(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return Result<ShellCommand>.Fail(ProblemCodes.EditorMissing, "No editor command is configured.");
        try
        {
            var (program, arguments) = CommandLine.Split(command);
            return Result<ShellCommand>.Ok(new ShellCommand(program, arguments));
        }
        catch (StackerException ex) { return Result<ShellCommand>.Fail(ProblemCodes.EditorMissing, ex.Message); }
    }
    /// <summary>The value Git needs for GIT_EDITOR: git runs it through a shell, so it must be quoted there.</summary>
    /// <remarks>Every token is quoted when needed and the results are joined with single spaces. Escaping the four
    /// shell-sensitive characters inside the double quotes prevents a path from being re-split or a substitution from
    /// being evaluated by that shell.</remarks>
    /// <returns>The shell-ready command line.</returns>
    public string ForGitEditor() => string.Join(' ', new[] { Quote(Program) }.Concat(Arguments.Select(Quote)));
    /// <summary>The arguments for a detached launch, with the given paths appended.</summary>
    /// <remarks>No quoting is applied: the process API receives each argument separately, so a path with spaces or
    /// shell characters is passed literally.</remarks>
    /// <param name="paths">Files to open after the configured arguments.</param>
    /// <returns>The complete argument list.</returns>
    public IReadOnlyList<string> ArgumentsFor(IEnumerable<string> paths) => [.. Arguments, .. paths];
    // Backslash is written as its code point so this file never needs an escape sequence.
    /// <summary>The backslash escape character, written as code point 92 so this file needs no escape sequence
    /// for it.</summary>
    private const char Backslash = (char)92;
    /// <summary>Wraps a token in double quotes when the shell would otherwise split or interpret it.</summary>
    /// <remarks>Inside the quotes, backslash, double quote, dollar and backtick are escaped with
    /// <see cref="Backslash"/>; every other character is copied literally, so single quotes and spaces need no special
    /// handling.</remarks>
    /// <param name="value">The token to quote.</param>
    /// <returns>The token unchanged, or the double-quoted and escaped form.</returns>
    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(NeedsQuoting)) return value;
        var builder = new System.Text.StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            if (character is '"' or '$' or '`' || character == Backslash) builder.Append(Backslash);
            builder.Append(character);
        }
        return builder.Append('"').ToString();
    }
    /// <summary>Reports whether a character forces the quoted form.</summary>
    /// <remarks>Whitespace and the shell control characters split a command; quotes and
    /// <see cref="Backslash"/> change quoting; dollar and backtick start substitutions. Any of them means the raw token
    /// cannot be used.</remarks>
    /// <param name="character">The character to test.</param>
    /// <returns>True when the token must be quoted.</returns>
    private static bool NeedsQuoting(char character) => char.IsWhiteSpace(character)
        || character is '"' or '\'' || character == Backslash || character is '$' or '`' or '&' or '|' or ';' or '<' or '>' or '(' or ')';
    /// <summary>Returns the Git-editor form, so interpolation produces a value Git could use.</summary>
    /// <returns>The same text as <see cref="ForGitEditor"/>.</returns>
    public override string ToString() => ForGitEditor();
}

/// <summary>A stash commit recorded so the saved changes can be restored later.</summary>
/// <remarks>The stash is identified by its full commit id rather than by <c>stash@{n}</c>, because the index shifts
/// whenever another stash is added or dropped; an index recorded before a restart could restore the wrong changes.
/// Restore finds this id in the stash list and pops the matching index, so newer stashes are left alone.</remarks>
public readonly record struct StashRef
{
    /// <summary>The commit id of the stash commit.</summary>
    public CommitSha Commit { get; }
    /// <summary>Stores a validated stash commit; <see cref="Parse"/> is the public entry point.</summary>
    /// <param name="commit">The validated stash commit.</param>
    private StashRef(CommitSha commit) => Commit = commit;
    /// <summary>Validates a stash commit id.</summary>
    /// <remarks>Uses the same complete-id rule as <see cref="CommitSha"/>, so an abbreviation or symbolic stash
    /// name fails with <see cref="ProblemCodes.StateInvalid"/>; the message is the generic invalid-commit-id text
    /// rather than a stash-specific one.</remarks>
    /// <param name="value">The candidate stash commit id.</param>
    /// <returns>The stash reference, or the commit-id problem.</returns>
    public static Result<StashRef> Parse(string? value) => CommitSha.Parse(value).Map(commit => new StashRef(commit));
    /// <summary>Returns the full stash commit id.</summary>
    /// <returns>The commit id.</returns>
    public override string ToString() => Commit.Value;
}
