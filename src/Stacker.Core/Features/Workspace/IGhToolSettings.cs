using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>The GitHub CLI binary and the credential source the CLI should use.</summary>
/// <remarks>Settings are pushed into the adapter once after they load, so a changed path or credential choice
/// applies to every later command without rebuilding the adapter.</remarks>
public interface IGhToolSettings
{
    /// <summary>Executable path from settings, or <see langword="null"/> to look on PATH.</summary>
    string? Override
    {
        get; set;
    }
    /// <summary>Whether inherited GitHub token variables are removed from the child environment so the CLI resolves
    /// the account the user saved with its own login.</summary>
    /// <remarks>Without this, a token exported for another tool would override the saved account and reads or writes
    /// could reach a different identity than the one the user selected. Both the public and enterprise token pairs
    /// are removed when this is true.</remarks>
    bool UseSavedCredentials
    {
        get; set;
    }
}
