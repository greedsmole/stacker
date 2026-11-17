using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Content of one Git blob, together with the object id used as its content identity in caches.</summary>
/// <param name="Id">Blob object id, used as the cache key so two revisions of the same text are not re-tokenized.</param>
/// <param name="Text">Decoded content. A missing path or a blob past the display limit is reported as no content instead of
/// a partial or truncated text.</param>
public sealed record BlobContent(string Id, string Text);
