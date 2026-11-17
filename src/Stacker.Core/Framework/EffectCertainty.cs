namespace Stacker.Core.Framework;

/// <summary>How sure a boundary is that an effect reached its destination.</summary>
/// <remarks>This is the fact that decides whether a failed operation may be retried. A never-started or rejected
/// write did not land, a confirmed one did, and an unknown one may have landed and must be reconciled by inspecting
/// the destination. <see cref="Problem.Certainty"/> defaults to unknown, and the command outcome factories set only
/// the value they can prove, so an unclassified failure is never presented as safe to retry.</remarks>
public enum EffectCertainty
{
    /// <summary>No effect was attempted: the command was refused, or cancellation happened before dispatch.</summary>
    NotStarted,
    /// <summary>The destination answered with a definitive refusal (for example a GitHub 401, 403, 404 or 422), so
    /// the write did not land; fixing the cause and retrying is safe.</summary>
    Rejected,
    /// <summary>The effect is known to have landed.</summary>
    Confirmed,
    /// <summary>The effect was dispatched but the answer was lost, so it may or may not have landed. Never retried
    /// automatically; a reload or the user must reconcile it.</summary>
    Unknown
}
