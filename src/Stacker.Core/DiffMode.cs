namespace Stacker.Core;

/// <summary>Selects the layer comparisons one diff request asks for.</summary>
/// <remarks>The mode decides both which positions are used and which parent side each comparison takes, so the same request can describe
/// "this layer", "everything through this layer", "the whole stack" or several independent layers. <see cref="DiffService"/> normalizes the
/// request from the mode rather than trusting the caller to send matching positions.</remarks>
public enum DiffMode
{
    /// <summary>One layer against its immediate parent, with the stack base as the parent of the first layer.</summary>
    Layer,
    /// <summary>The whole path from the stack base through a layer, compared as one base-to-layer diff.</summary>
    Cumulative,
    /// <summary>The entire stack from the base to the topmost layer; positions other than the last are ignored.</summary>
    FullStack,
    /// <summary>Each selected layer against its own parent, shown as independent sections.</summary>
    MultiLayer
}
