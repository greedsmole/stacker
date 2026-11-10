namespace Stacker.Core;

/// <summary>Splits a user-authored command into a program and its arguments without ever involving a shell.</summary>
/// <remarks>Used for user-authored editor and tool commands, where a path with spaces must survive as one argument and no shell
/// syntax is allowed. Quotes are grouping delimiters only: there is no escape character, no variable expansion and no globbing, so
/// an argument cannot contain a quote character at all. Only spaces and tabs separate arguments; any other whitespace stays inside
/// the token. An unterminated quote is not an error: everything after the opening quote becomes part of the final argument, so a
/// missing closing quote silently merges the arguments the user meant to keep apart instead of refusing the launch.</remarks>
public static class CommandLine
{
    /// <summary>Honours double and single quotes so a path with spaces stays one argument.</summary>
    /// <remarks>A single pass over the characters. Outside quotes a space or tab ends the token, either quote opens a quoted
    /// section, and everything else is appended. Inside a quoted section every character is literal until the matching quote; because
    /// a quote also marks the token as started, <c>""</c> produces an empty argument rather than being skipped. A quote may start or resume in the middle
    /// of a token, so <c>foo"bar baz"qux</c> is one argument. The final token is flushed without needing a trailing space. The first
    /// token is the program: an empty command, or a first token that comes out empty, is rejected because there would be nothing to
    /// launch. The returned arguments are the remaining tokens in order, and the list is empty when the command is only a program.</remarks>
    /// <param name="command">The command line as the user typed it, for example <c>"/Applications/My Editor/bin/edit" -w</c>.</param>
    /// <returns>The program followed by its arguments.</returns>
    /// <exception cref="StackerException">The command is empty or whitespace, or its first token is an empty quoted string.</exception>
    public static (string Program, IReadOnlyList<string> Arguments) Split(string command)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var started = false;
        foreach (var character in command)
        {
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else current.Append(character);
                continue;
            }
            switch (character)
            {
                case '"':
                case '\'': quote = character; started = true; break;
                case ' ':
                case '\t':
                    if (started || current.Length > 0) { parts.Add(current.ToString()); current.Clear(); started = false; }
                    break;
                default: current.Append(character); started = true; break;
            }
        }
        if (started || current.Length > 0) parts.Add(current.ToString());
        if (parts.Count == 0 || parts[0].Length == 0) throw new StackerException("The editor command is empty.");
        return (parts[0], parts.Skip(1).ToArray());
    }
}
