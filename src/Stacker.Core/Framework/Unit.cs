namespace Stacker.Core.Framework;

/// <summary>The single value passed around when a call has no result of its own.</summary>
public readonly record struct Unit
{
    /// <summary>The only value the type has; exists so a void-like effect can still return
    /// <see cref="Result{T}"/>.</summary>
    public static readonly Unit Value = default;
}
