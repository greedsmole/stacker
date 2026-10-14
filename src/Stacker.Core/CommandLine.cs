namespace Stacker.Core;

/// <summary>Splits a user-authored command into a program and its arguments without ever involving a shell.</summary>
public static class CommandLine
{
    /// <summary>Honours double and single quotes so a path with spaces stays one argument.</summary>
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
