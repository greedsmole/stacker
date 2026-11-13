namespace Stacker.Core.Framework;

/// <summary>Validates journal changes against the current stored phase and step evidence.</summary>
public static class JournalTransitions
{
    /// <summary>Checks a normal command close or an explicit restack recovery decision.</summary>
    public static bool CanClose(JournalEntry entry, JournalPhase next) => (entry.Phase, next) switch
    {
        (var current, var target) when current == target => true,
        (JournalPhase.Running, JournalPhase.Applied or JournalPhase.NeedsUser or JournalPhase.RolledBack or JournalPhase.Dismissed) => true,
        (JournalPhase.NeedsUser, JournalPhase.Applied or JournalPhase.RolledBack or JournalPhase.Dismissed) => true,
        (JournalPhase.Running or JournalPhase.NeedsUser or JournalPhase.Applied, JournalPhase.Undone)
            or (JournalPhase.Applied, JournalPhase.Dismissed) =>
            entry.Kind == "restack" && entry.Moved.Count > 0,
        _ => false
    };

    /// <summary>Checks a step update, including reconciliation of an interrupted rebase.</summary>
    public static bool CanRecordStep(JournalEntry entry, JournalStep current, JournalStep next)
    {
        if (current == next)
            return true;
        if (!entry.NeedsAttention || current.Id != next.Id || current.Kind != next.Kind || current.Target != next.Target ||
            current.Kind == "original" || (current.OldSha is not null && current.OldSha != next.OldSha) ||
            (current.NewSha is not null && current.NewSha != next.NewSha))
            return false;

        return (current.State, next.State) switch
        {
            (StepState.Planned, StepState.Started) => true,
            (StepState.Started, StepState.Done or StepState.Failed or StepState.Unknown) => true,
            (StepState.Unknown, StepState.Done) => entry.Kind == "restack" && current.Kind == "rebase" &&
                entry.PendingStepId == current.Id && current.OldSha is not null && next.NewSha is not null,
            _ => false
        };
    }
}
