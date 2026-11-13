using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Core;
namespace Stacker.Desktop;

/// <summary>One selectable start point for a new layer, labelled for the combo box.</summary>
public sealed record LayerStart(string Label, string Reference)
{
    public override string ToString() => Label;
}
