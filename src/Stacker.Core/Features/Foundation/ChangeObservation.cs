using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One recorded observation: which version a change pointed at, when it was seen and in which order.</summary>
/// <param name="VersionId">Id of the observed <see cref="ChangeVersion"/>.</param>
/// <param name="SeenAt">Moment the version was observed.</param>
/// <param name="Sequence">Strictly increasing ordering value, assigned by the store when the observation row is appended.</param>
/// <remarks>Mirrors a row of the observation log. No code in <c>src</c> currently constructs or reads this type: the
/// store appends observation rows while saving versions and reads only the newest one per change.</remarks>
public sealed record ChangeObservation(string VersionId, DateTimeOffset SeenAt, long Sequence);
