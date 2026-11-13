using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Resolves and opens the text editor the user chose, without ever involving a shell.</summary>
/// <remarks>Resolution is layered so a user can rely on Git's own configuration without repeating it in Stacker, while a machine
/// with no editor configured still gets a usable default. Both methods return the command text that was resolved, so the caller
/// can show the user exactly what will run instead of guessing.</remarks>
public interface IEditorLauncher
{
    /// <summary>Explicit setting, then git core.editor, then GIT_EDITOR/VISUAL/EDITOR, then the platform default.</summary>
    /// <remarks>Each candidate is trimmed and a blank one falls through to the next, so an empty setting never wins. Reading Git's
    /// configuration is a bounded, non-interactive read that does not change repository state. The platform default is supplied by
    /// the adapter, which is why this port normally resolves something even on a bare system; null is reserved for an adapter that
    /// truly cannot resolve any command.</remarks>
    /// <param name="root">Repository root used to read Git's configuration.</param>
    /// <param name="overrideCommand">The user's saved editor setting, or null to fall back to the layers below it.</param>
    /// <param name="ct">Cancels the configuration read.</param>
    /// <returns>The command text to show and reuse, or <see langword="null"/> when no candidate resolved.</returns>
    Task<Result<string?>> ResolveAsync(string root, string? overrideCommand, CancellationToken ct = default);
    /// <summary>Opens the paths in the resolved editor and returns the command that was used, or null when none resolved.</summary>
    /// <remarks>Paths are appended as separate arguments after the command, so a path with spaces needs no quoting here and never
    /// reaches a shell. The launch is detached: this method returns as soon as the editor process has started, and the returned
    /// command is only a report of what was launched.</remarks>
    /// <param name="root">Repository root used to resolve the editor when no setting is supplied.</param>
    /// <param name="paths">Files to open, passed as separate arguments.</param>
    /// <param name="overrideCommand">The user's saved editor setting, or null to resolve from the usual order.</param>
    /// <param name="ct">Cancels the resolution step; the launched editor is not affected.</param>
    /// <returns>The command that was launched, or <see langword="null"/> when nothing could be resolved.</returns>
    Task<Result<string?>> OpenAsync(string root, IReadOnlyList<string> paths, string? overrideCommand, CancellationToken ct = default);
}
