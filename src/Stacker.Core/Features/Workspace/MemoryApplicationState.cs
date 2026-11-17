using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Explicit transient state for previews/tests without an OS-owned application store.</summary>
/// <remarks>Documents are held as JSON text rather than as objects, so a value is never shared between the writer
/// and the reader and members excluded from serialization do not survive the round-trip, exactly as with the profile
/// implementation. Nothing is persisted and nothing is shared between instances: the same instance must be handed to
/// every feature that should see the same state, and its lifetime is the process. Used as the fallback when a host
/// supplies no store, which keeps a preview or a test from writing into the real user profile.</remarks>
public sealed class MemoryApplicationState : IApplicationState
{
    /// <summary>Serialized documents keyed by (kind, identity); tuple equality is ordinal on both strings.</summary>
    private readonly Dictionary<(string, string), string> _values = [];
    /// <summary>A synthetic root. The value is relative, so a caller that combines it into a path resolves it
    /// against the process working directory; hosts that persist anything use the profile implementation.</summary>
    public string Root => "transient";
    /// <summary>Always <see langword="null"/>: nothing here can be damaged or fail to write, so there is no
    /// diagnostic to report.</summary>
    public string? LastProblem => null;
    /// <summary>Deserializes the stored document, or reports no document for an identity that was never written.</summary>
    /// <typeparam name="T">Reference type of the stored document.</typeparam>
    /// <param name="kind">Document family.</param>
    /// <param name="identity">Logical key within the family.</param>
    /// <param name="ct">Ignored; there is no I/O to cancel.</param>
    /// <returns>The deserialized document, and always a success: this store has no damaged data to report.</returns>
    public Task<Result<T?>> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class =>
        Task.FromResult(Result<T?>.Ok(_values.TryGetValue((kind, identity), out var json) ? System.Text.Json.JsonSerializer.Deserialize<T>(json) : null));
    /// <summary>Serializes the document into the in-memory table, replacing any previous value for the identity.</summary>
    /// <typeparam name="T">Reference type of the stored document.</typeparam>
    /// <param name="kind">Document family.</param>
    /// <param name="identity">Logical key within the family.</param>
    /// <param name="value">The complete document to keep.</param>
    /// <param name="ct">Ignored; there is no I/O to cancel.</param>
    /// <returns>Always <see cref="Result.Ok"/>, since no write can fail here.</returns>
    public Task<Result<Unit>> WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class
    {
        _values[(kind, identity)] = System.Text.Json.JsonSerializer.Serialize(value);
        return Task.FromResult(Result.Ok);
    }
}
