using System.Text.RegularExpressions;
namespace Stacker.Core.Framework;

/// <summary>A commit message that is safe to pass to git as a single argument.</summary>
/// <remarks>The text is trimmed and must be non-empty, because Git would otherwise refuse the commit or record a
/// meaningless message. The 8000-character cap bounds what reaches the Git argument list and the UI; no such Git limit
/// is encoded here and the rationale for 8000 is not recorded. The process boundary passes each argument separately,
/// so quoting is not the reason for the cap, and newlines inside the message are preserved.</remarks>
public readonly record struct CommitMessage
{
    /// <summary>The trimmed message.</summary>
    public string Value
    {
        get;
    }
    /// <summary>Stores an already validated message; <see cref="Parse"/> is the public entry point.</summary>
    /// <param name="value">The trimmed message.</param>
    private CommitMessage(string value) => Value = value;
    /// <summary>Trims a user-authored message and rejects empty or oversized text.</summary>
    /// <param name="text">The raw text from the editor; may be null.</param>
    /// <returns>The message, or <see cref="ProblemCodes.StateInvalid"/> with a display-ready reason.</returns>
    public static Result<CommitMessage> Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return Result<CommitMessage>.Fail(ProblemCodes.StateInvalid, "Enter a commit message.");
        if (trimmed.Length > 8000)
            return Result<CommitMessage>.Fail(ProblemCodes.StateInvalid, "The commit message is too long.");
        return Result<CommitMessage>.Ok(new CommitMessage(trimmed));
    }
    /// <summary>Returns the trimmed message.</summary>
    /// <returns>The stored text.</returns>
    public override string ToString() => Value;
}
