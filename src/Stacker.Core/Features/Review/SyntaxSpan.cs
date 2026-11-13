using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One syntax-colored character range in a source line.</summary>
/// <param name="Start">Zero-based character offset of the span inside its line.</param>
/// <param name="Length">Number of characters the span covers; a zero-length range carries no styling and the shipped
/// highlighter does not emit one.</param>
/// <param name="Color">Foreground color string supplied by the active theme.</param>
/// <param name="Italic">Optional italic styling; the shipped highlighter leaves it at its default.</param>
public sealed record SyntaxSpan(int Start, int Length, string Color, bool Italic = false);
