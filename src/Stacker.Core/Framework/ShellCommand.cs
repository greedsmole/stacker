using System.Text.RegularExpressions;
namespace Stacker.Core.Framework;

/// <summary>An editor or tool command, kept as a program plus arguments so no shell is ever involved.</summary>
/// <remarks>A user-authored command is split once into a program and arguments; Stacker launches it directly, so
/// spaces and metacharacters stay literal in an argument. Git is different: it evaluates the GIT_EDITOR value through
/// a shell, so <see cref="ForGitEditor"/> re-quotes each token. A blank command fails with
/// <see cref="ProblemCodes.EditorMissing"/> so Git never receives an empty editor value.</remarks>
public readonly record struct ShellCommand
{
    /// <summary>The executable to launch, exactly as the user wrote it (after quote removal).</summary>
    public string Program
    {
        get;
    }
    /// <summary>The arguments that follow the executable, each already separated.</summary>
    public IReadOnlyList<string> Arguments
    {
        get;
    }
    /// <summary>Stores an already split command; <see cref="Parse"/> is the public entry point.</summary>
    /// <param name="program">The executable.</param>
    /// <param name="arguments">The separated arguments.</param>
    private ShellCommand(string program, IReadOnlyList<string> arguments)
    {
        Program = program;
        Arguments = arguments;
    }
    /// <summary>Splits a configured editor command without invoking a shell.</summary>
    /// <remarks>Quoted paths with spaces stay one argument. A blank value, and a command the splitter rejects, both
    /// become <see cref="ProblemCodes.EditorMissing"/> with display-ready text, because there is no usable editor to
    /// launch.</remarks>
    /// <param name="command">The raw configured command.</param>
    /// <returns>The parsed command, or a problem when no editor can be launched.</returns>
    public static Result<ShellCommand> Parse(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Result<ShellCommand>.Fail(ProblemCodes.EditorMissing, "No editor command is configured.");
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
        if (value.Length > 0 && !value.Any(NeedsQuoting))
            return value;
        var builder = new System.Text.StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            if (character is '"' or '$' or '`' || character == Backslash)
                builder.Append(Backslash);
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
