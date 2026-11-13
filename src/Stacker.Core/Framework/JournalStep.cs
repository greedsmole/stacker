using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>One effect inside a command: a branch move, a stash, a commit, a remote write.</summary>
/// <remarks>The pair <paramref name="OldSha"/> and <paramref name="NewSha"/> is the recovery evidence: the old
/// commit is what an undo restores, and the new commit proves the ref has not moved since. Steps are immutable;
/// <see cref="Finished"/> produces the completed copy that <see cref="IJournal.StepAsync"/> stores.</remarks>
/// <param name="Id">Stable identifier inside the entry. Updates replace the step with the same id, so it must not
/// change between planning and completion.</param>
/// <param name="Kind">Effect category used for filtering and display: <c>rebase</c>, <c>push</c>, <c>original</c>,
/// or the command kind for a single-effect command. Recovery only restores <c>rebase</c> steps.</param>
/// <param name="Target">The ref or remote identifier the effect changes; shown to the user in an outcome's
/// effects list.</param>
/// <param name="OldSha">The commit to restore on undo, when known; <see langword="null"/> when the effect does not
/// replace a ref. Recovery also compares the current ref against it to avoid discarding newer work.</param>
/// <param name="NewSha">The commit the effect produced, when known; undo refuses to move a ref that no longer
/// points here.</param>
/// <param name="State">Last observed state; see <see cref="StepState"/> for who records each transition.</param>
public sealed record JournalStep(string Id, string Kind, string Target, string? OldSha, string? NewSha, StepState State)
{
    /// <summary>The approved restack plan, attached to the synthetic <c>original</c> step; the journal lifts it onto
    /// the entry so Continue can resume the remaining layers after a restart without replanning against a changed
    /// repository.</summary>
    public RestackPlan? RestackPlan
    {
        get; init;
    }
    /// <summary>Creates a step in <see cref="StepState.Planned"/> with no observed commits, which is the only state
    /// a plan may hand to the journal.</summary>
    /// <param name="id">Stable identifier inside the entry.</param>
    /// <param name="kind">Effect category, such as <c>rebase</c> or <c>push</c>.</param>
    /// <param name="target">The ref or remote identifier the effect will change.</param>
    /// <returns>A planned step with both commit fields empty.</returns>
    public static JournalStep Planned(string id, string kind, string target) => new(id, kind, target, null, null, StepState.Planned);
    /// <summary>Returns a copy carrying the observed commits and terminal state; the planned id, kind and target
    /// are preserved so recovery can match the step to the effect it describes.</summary>
    /// <param name="oldSha">The commit the effect replaced, or <see langword="null"/> when none applies.</param>
    /// <param name="newSha">The commit the effect produced, or <see langword="null"/> when unknown.</param>
    /// <param name="state">The state to record, normally <see cref="StepState.Done"/> or
    /// <see cref="StepState.Failed"/>.</param>
    /// <returns>A completed copy of this step.</returns>
    public JournalStep Finished(string? oldSha, string? newSha, StepState state) => this with { OldSha = oldSha, NewSha = newSha, State = state };
}
