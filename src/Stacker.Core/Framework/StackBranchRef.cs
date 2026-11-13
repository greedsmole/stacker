using System.Text.RegularExpressions;
namespace Stacker.Core.Framework;

/// <summary>A stack layer ref, tagged with whether Stacker may rewrite it.</summary>
/// <remarks>Exactly one side is set by the private constructor. Parsing tries the local namespace first, and the
/// properties that callers use for display and Git calls dereference the non-null side. <see cref="RequireLocal"/>
/// turns a read-only layer into an actionable refusal instead of letting a caller treat it as writable.</remarks>
public readonly record struct StackBranchRef
{
    /// <summary>The local side when this value is rewritable; <see langword="null"/> for a remote-tracked ref.</summary>
    public LocalBranch? Local
    {
        get;
    }
    /// <summary>The remote-tracking side when this value is read-only; <see langword="null"/> for a local ref.</summary>
    public RemoteTrackingRef? Remote
    {
        get;
    }
    /// <summary>Stores the parsed side; callers obtain values through <see cref="Parse"/>, which sets exactly
    /// one.</summary>
    /// <param name="local">The local side, when present.</param>
    /// <param name="remote">The remote side, when present.</param>
    private StackBranchRef(LocalBranch? local, RemoteTrackingRef? remote)
    {
        Local = local;
        Remote = remote;
    }
    /// <summary>True when this ref may be rewritten locally.</summary>
    public bool IsLocal => Local is not null;
    /// <summary>The full ref of whichever side is set.</summary>
    public string Ref => Local?.Ref ?? Remote!.Value.Ref;
    /// <summary>The display name: the local short name, or the remote's branch name without the remote.</summary>
    public string Name => Local?.Name ?? Remote!.Value.BranchName;
    /// <summary>Parses a local branch first, then a remote-tracking reference.</summary>
    /// <remarks>A null reference and any other namespace both fail with <see cref="ProblemCodes.StateInvalid"/>; the
    /// unsupported case repeats the raw reference so the user can see what was persisted.</remarks>
    /// <param name="reference">The full reference to classify.</param>
    /// <returns>The classified ref, or a problem when it belongs to neither namespace.</returns>
    public static Result<StackBranchRef> Parse(string? reference)
    {
        if (reference is null)
            return Result<StackBranchRef>.Fail(ProblemCodes.StateInvalid, "A branch reference is required.");
        var local = LocalBranch.Parse(reference);
        if (local.IsOk)
            return Result<StackBranchRef>.Ok(new(local.Value, null));
        var remote = RemoteTrackingRef.Parse(reference);
        return remote.IsOk ? Result<StackBranchRef>.Ok(new(null, remote.Value)) : Result<StackBranchRef>.Fail(ProblemCodes.StateInvalid, $"Unsupported reference: {reference}");
    }
    /// <summary>Requires a rewritable layer.</summary>
    /// <remarks>The publication planner calls this before treating a layer as writable. A remote-tracked layer
    /// cannot be restacked without changing origin, so the failure names the local branch the user should select
    /// instead.</remarks>
    /// <returns>The local branch, or <see cref="ProblemCodes.GitNotLocalBranch"/> with guidance.</returns>
    public Result<LocalBranch> RequireLocal() => Local is { } branch
        ? Result<LocalBranch>.Ok(branch)
        : Result<LocalBranch>.Fail(ProblemCodes.GitNotLocalBranch, $"{Name} is tracked from a remote and cannot be restacked. Use your local branch of the same name.");
    /// <summary>Returns the display name.</summary>
    /// <returns>The short local name or the remote branch name.</returns>
    public override string ToString() => Name;
}
