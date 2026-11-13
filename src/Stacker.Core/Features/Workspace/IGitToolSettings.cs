using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>The Git binary Stacker should run, as a configured path or as a resolved one.</summary>
/// <remarks>Resolution searches PATH and the usual installation locations, so the implementation caches the answer
/// instead of repeating the search for every command. Assigning <see cref="Override"/> is the invalidation signal:
/// the next <see cref="Resolve"/> call resolves again.</remarks>
public interface IGitToolSettings
{
    /// <summary>Executable path from settings, or <see langword="null"/> to search PATH and the common install
    /// locations.</summary>
    /// <remarks>Assigning clears any cached resolution, so an edited setting takes effect without a restart.</remarks>
    string? Override
    {
        get; set;
    }
    /// <summary>Returns the executable to use now, resolving and remembering it on first use.</summary>
    /// <returns>A full path when a candidate was found, otherwise the bare program name.</returns>
    /// <remarks>Returning the bare name instead of an empty value keeps one behaviour at the boundary: the process
    /// fails to start and the failure names the missing tool, rather than the caller silently running whatever a
    /// later PATH lookup happens to find.</remarks>
    string Resolve();
}
