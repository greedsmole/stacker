using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>How far one journaled effect got.</summary>
/// <remarks>The journal records an effect before it runs and its outcome as soon as that outcome is known,
/// because the stored entry is the only evidence recovery has after a crash. <see cref="StepState.Done"/>,
/// <see cref="StepState.Started"/> and <see cref="StepState.Unknown"/> may all describe something that took effect;
/// <see cref="StepState.Planned"/> and <see cref="StepState.Failed"/> prove that nothing moved. The numeric values are
/// part of the stored format (the journal is serialized JSON with the default enum encoding), so members must not be
/// reordered or renumbered without a schema migration. Single-effect commands let <c>EffectCommand</c> advance these
/// states; multi-step adapters (restack, push) record each step themselves.</remarks>
public enum StepState
{
    /// <summary>Written by <see cref="IJournal.OpenAsync"/> from the plan, before any effect runs.</summary>
    Planned,
    /// <summary>Recorded after the effect returned success; the replaced and resulting commits are then known.</summary>
    Done,
    /// <summary>Recorded when the effect failed in a way that proves nothing changed, so it is safe to abandon.</summary>
    Failed,
    /// <summary>Recorded immediately before an effect whose completion cannot be observed atomically, so a
    /// process death between the effect and its record still leaves evidence that something may have happened.</summary>
    Started,
    /// <summary>Recorded when the effect was dispatched but its result was lost, so it may or may not have
    /// happened; recovery inspects instead of retrying.</summary>
    Unknown
}
