namespace Stacker.Core;

/// <summary>Marks a GitHub authentication failure so the shell can offer sign-in instead of a generic error.</summary>
/// <remarks>A failed result whose problem code is <see cref="Framework.ProblemCodes.GitHubAuth"/> is rethrown as this type when a result is
/// unwrapped, so a review or sync screen can check for it without matching message text. The token variables that can override gh's saved
/// account are described on <see cref="AppSettings.UseSavedGhCredentials"/>.</remarks>
public sealed class GitHubAuthenticationException : StackerException
{
    /// <summary>Creates an authentication failure with display text but no catalogue code.</summary>
    /// <param name="message">Text shown to the user.</param>
    public GitHubAuthenticationException(string message) : base(message) { }
    /// <summary>Creates an authentication failure that keeps the problem's code and certainty.</summary>
    /// <param name="problem">The catalogued problem, normally code <c>github.auth</c>.</param>
    public GitHubAuthenticationException(Framework.Problem problem) : base(problem) { }
}
