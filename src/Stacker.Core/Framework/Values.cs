using System.Text.RegularExpressions;
namespace Stacker.Core.Framework;

/// <summary>A repository root exactly as Git reports it, so every store key is stable.</summary>
public readonly record struct RepoRoot
{
    public string Value { get; }
    private RepoRoot(string value) => Value = value;
    public static RepoRoot From(string canonical) => new(canonical);
    public static Result<RepoRoot> Parse(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Result<RepoRoot>.Fail(ProblemCodes.StateInvalid, "A repository root is required.") : Result<RepoRoot>.Ok(new RepoRoot(path));
    public override string ToString() => Value;
}

/// <summary>A complete commit id; short ids and revisions are rejected.</summary>
public readonly partial record struct CommitSha
{
    public string Value { get; }
    private CommitSha(string value) => Value = value;
    [GeneratedRegex("\\A(?:[0-9a-f]{40}|[0-9a-f]{64})\\z")]
    private static partial Regex Pattern();
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);
    public static Result<CommitSha> Parse(string? value) =>
        IsValid(value) ? Result<CommitSha>.Ok(new CommitSha(value!)) : Result<CommitSha>.Fail(ProblemCodes.StateInvalid, "Invalid commit id.");
    public override string ToString() => Value;
}

/// <summary>A local branch reference: the only thing Stacker is ever allowed to rewrite.</summary>
public readonly record struct LocalBranch
{
    public const string Prefix = "refs/heads/";
    public string Ref { get; }
    public string Name { get; }
    private LocalBranch(string reference) { Ref = reference; Name = reference[Prefix.Length..]; }
    public static Result<LocalBranch> Parse(string? reference) =>
        reference is not null && reference.StartsWith(Prefix, StringComparison.Ordinal) && reference.Length > Prefix.Length && !reference.Any(char.IsControl) && !reference.Contains(' ')
            ? Result<LocalBranch>.Ok(new LocalBranch(reference))
            : Result<LocalBranch>.Fail(ProblemCodes.GitNotLocalBranch, $"{StackRefs.Short(reference ?? "")} is not a local branch, so Stacker cannot rewrite it.");
    public override string ToString() => Name;
}

/// <summary>A remote-tracking reference, which can be compared against but never rewritten.</summary>
public readonly record struct RemoteTrackingRef
{
    public const string Prefix = "refs/remotes/";
    public string Ref { get; }
    public string ShortName { get; }
    private RemoteTrackingRef(string reference) { Ref = reference; ShortName = reference[Prefix.Length..]; }
    public static Result<RemoteTrackingRef> Parse(string? reference) =>
        reference is not null && reference.StartsWith(Prefix, StringComparison.Ordinal) && reference.Length > Prefix.Length
            ? Result<RemoteTrackingRef>.Ok(new RemoteTrackingRef(reference))
            : Result<RemoteTrackingRef>.Fail(ProblemCodes.StateInvalid, "Not a remote-tracking reference.");
    /// <summary>The branch name a GitHub pull request would use for this ref ("origin/main" becomes "main").</summary>
    public string BranchName => ShortName.Contains('/') ? ShortName[(ShortName.IndexOf('/') + 1)..] : ShortName;
    public override string ToString() => ShortName;
}

/// <summary>A stack layer ref, tagged with whether Stacker may rewrite it.</summary>
public readonly record struct StackBranchRef
{
    public LocalBranch? Local { get; }
    public RemoteTrackingRef? Remote { get; }
    private StackBranchRef(LocalBranch? local, RemoteTrackingRef? remote) { Local = local; Remote = remote; }
    public bool IsLocal => Local is not null;
    public string Ref => Local?.Ref ?? Remote!.Value.Ref;
    public string Name => Local?.Name ?? Remote!.Value.BranchName;
    public static Result<StackBranchRef> Parse(string? reference)
    {
        if (reference is null) return Result<StackBranchRef>.Fail(ProblemCodes.StateInvalid, "A branch reference is required.");
        var local = LocalBranch.Parse(reference);
        if (local.IsOk) return Result<StackBranchRef>.Ok(new(local.Value, null));
        var remote = RemoteTrackingRef.Parse(reference);
        return remote.IsOk ? Result<StackBranchRef>.Ok(new(null, remote.Value)) : Result<StackBranchRef>.Fail(ProblemCodes.StateInvalid, $"Unsupported reference: {reference}");
    }
    /// <summary>Requires a rewritable layer.</summary>
    public Result<LocalBranch> RequireLocal() => Local is { } branch
        ? Result<LocalBranch>.Ok(branch)
        : Result<LocalBranch>.Fail(ProblemCodes.GitNotLocalBranch, $"{Name} is tracked from a remote and cannot be restacked. Use your local branch of the same name.");
    public override string ToString() => Name;
}

/// <summary>A commit message that is safe to pass to git as a single argument.</summary>
public readonly record struct CommitMessage
{
    public string Value { get; }
    private CommitMessage(string value) => Value = value;
    public static Result<CommitMessage> Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return Result<CommitMessage>.Fail(ProblemCodes.StateInvalid, "Enter a commit message.");
        if (trimmed.Length > 8000) return Result<CommitMessage>.Fail(ProblemCodes.StateInvalid, "The commit message is too long.");
        return Result<CommitMessage>.Ok(new CommitMessage(trimmed));
    }
    public override string ToString() => Value;
}

/// <summary>An editor or tool command, kept as a program plus arguments so no shell is ever involved.</summary>
public readonly record struct ShellCommand
{
    public string Program { get; }
    public IReadOnlyList<string> Arguments { get; }
    private ShellCommand(string program, IReadOnlyList<string> arguments) { Program = program; Arguments = arguments; }
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
    public string ForGitEditor() => string.Join(' ', new[] { Quote(Program) }.Concat(Arguments.Select(Quote)));
    /// <summary>The arguments for a detached launch, with the given paths appended.</summary>
    public IReadOnlyList<string> ArgumentsFor(IEnumerable<string> paths) => [.. Arguments, .. paths];
    // Backslash is written as its code point so this file never needs an escape sequence.
    private const char Backslash = (char)92;
    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(NeedsQuoting)) return value;
        var builder = new System.Text.StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            if (character == '"' || character == Backslash) builder.Append(Backslash);
            builder.Append(character);
        }
        return builder.Append('"').ToString();
    }
    private static bool NeedsQuoting(char character) => char.IsWhiteSpace(character)
        || character == '"' || character == Backslash || character is '$' or '`' or '&' or '|' or ';' or '<' or '>' or '(' or ')';
    public override string ToString() => ForGitEditor();
}

/// <summary>A stash commit recorded so it can be restored by index later.</summary>
public readonly record struct StashRef
{
    public CommitSha Commit { get; }
    private StashRef(CommitSha commit) => Commit = commit;
    public static Result<StashRef> Parse(string? value) => CommitSha.Parse(value).Map(commit => new StashRef(commit));
    public override string ToString() => Commit.Value;
}
