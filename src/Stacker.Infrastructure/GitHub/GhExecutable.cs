using Stacker.Core;
namespace Stacker.Infrastructure;

/// <summary>Resolves the GitHub CLI executable and controls environment-token usage.</summary>
public sealed class GhExecutable : IGhToolSettings
{
    /// <summary>Ignores environment-provided tokens when the user explicitly chooses gh's saved account.</summary>
    public bool UseSavedCredentials
    {
        get; set;
    }
    /// <summary>Environment variables removed when saved gh credentials are selected.</summary>
    public IReadOnlyList<string> UnsetEnvironment => UseSavedCredentials ? ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"] : [];
    /// <summary>Optional executable path, primarily used when gh is installed outside PATH.</summary>
    public string? Override
    {
        get; set;
    }
    /// <summary>Returns the override, the first executable on PATH, common Homebrew paths, or gh for process lookup.</summary>
    public string Resolve() => !string.IsNullOrWhiteSpace(Override) ? Override :
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => Path.Combine(p, OperatingSystem.IsWindows() ? "gh.exe" : "gh"))
        .Concat(new[] { "/opt/homebrew/bin/gh", "/usr/local/bin/gh" }).FirstOrDefault(File.Exists) ?? "gh";
}

