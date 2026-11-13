using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

/// <summary>An unchanged range in both sides of a patch, before a particular rendered row.</summary>
public sealed record DiffContextGap(int BeforeRow, int OldStart, int NewStart, int Count, bool HasHunkAbove, bool HasHunkBelow)
{
    public const int ExpandStep = 20;
    public int RevealCount => Math.Min(Count, ExpandStep);
    public bool RevealFromEnd => !HasHunkAbove && HasHunkBelow;
}
