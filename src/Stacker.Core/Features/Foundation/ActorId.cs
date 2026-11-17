using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Opaque identifier for the person or automation attributed with an action.</summary>
/// <param name="Value">Caller-supplied identifier, compared verbatim and never normalised.</param>
/// <remarks>Declared as the attribution token for ports that need one; nothing in <c>src</c> currently constructs or
/// reads it, so it carries no behaviour of its own.</remarks>
public readonly record struct ActorId(string Value);
