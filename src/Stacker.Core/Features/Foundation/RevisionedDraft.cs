using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>A stored review draft together with the revision that a later save must match.</summary>
/// <param name="Draft">Draft payload as persisted.</param>
/// <param name="Revision">Revision the draft was read at; passing it back to the save call is what detects a concurrent
/// write from another window.</param>
public sealed record RevisionedDraft(ReviewDraft Draft, long Revision);
