using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>A blocking question the reviewer attached to one path of one change.</summary>
/// <remarks>Free-form text shown as <c>BLOCKER</c> beside the file, so an unresolved question cannot be mistaken for an
/// approval. This is local review state; nothing is published to GitHub. Several concerns for one path are merged when read,
/// and the UI clears one by removing it rather than by writing empty text.</remarks>
/// <param name="ChangeId">Change the concern belongs to.</param>
/// <param name="Path">Path the question is about.</param>
/// <param name="Text">The question itself; empty text is never stored as a concern.</param>
public sealed record ReviewConcern(string ChangeId, string Path, string Text);
