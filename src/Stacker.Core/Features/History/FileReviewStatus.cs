using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Per-file review verdict derived from a checkpoint and the comparison since then, with optional blocker text.</summary>
/// <remarks><see cref="Label"/> is the row text: current display path, the state, then <c>· BLOCKER: </c> plus the question
/// when one exists. Concerns are attached after the state is computed, so a blocker still shows on a file whose state is
/// <see cref="FileReviewState.Reviewed"/> — it stays visible until the reviewer withdraws it.</remarks>
/// <param name="Change">Current layer's file entry, which supplies the display path and the diff statistics.</param>
/// <param name="State">Classification computed by <see cref="ReviewProgressService.StatusAsync"/>.</param>
/// <param name="CheckedVersion">Version the checkpoint was taken on, or <see langword="null"/> when no usable checkpoint
/// exists.</param>
public sealed record FileReviewStatus(FileChange Change, FileReviewState State, ChangeVersion? CheckedVersion)
{
    /// <summary>Blocking question recorded for this path, attached after classification; <see langword="null"/> means clear.</summary>
    public string? Concern
    {
        get; init;
    }
    /// <summary>Row label built as display path, state and — when one exists — <c>· BLOCKER: </c> followed by the question.</summary>
    public string Label => $"{Change.DisplayPath} · {State}" + (Concern is null ? "" : " · BLOCKER: " + Concern);
}
