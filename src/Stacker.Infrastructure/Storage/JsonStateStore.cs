using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure.Framework;

/// <summary>One JSON document per identity; a damaged document is moved aside instead of thrown at the caller.</summary>
public sealed class JsonStateStore<T>(IAtomicFile files, string directory, bool quarantineCorrupt = true) : IStateStore<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    public string PathFor(string identity) => Path.Combine(directory, FileLockFactory.Hash(identity) + ".json");

    public Result<T?> Read(string identity)
    {
        var path = PathFor(identity);
        var bytes = files.Read(path);
        if (bytes.IsFailed)
            return Result<T?>.Fail(bytes.Problem!);
        if (bytes.Value is null)
            return Result<T?>.Ok(null);
        try
        {
            return Result<T?>.Ok(JsonSerializer.Deserialize<T>(bytes.Value));
        }
        catch (JsonException ex)
        {
            if (!quarantineCorrupt)
                return Result<T?>.Fail(ProblemCodes.StoreCorrupt,
                $"The recovery journal is damaged. Its original file was preserved at {path}. Resolve it before changing Git. ({ex.Message})");
            var moved = Quarantine(identity);
            var where = moved.IsOk ? $" A copy was kept at {System.IO.Path.GetFileName(moved.Value)}." : "";
            return Result<T?>.Fail(ProblemCodes.StoreCorrupt, $"A Stacker file was damaged and has been reset.{where} ({ex.Message})");
        }
    }

    public async Task<Result<Unit>> WriteAsync(string identity, T value, CancellationToken ct = default)
    {
        try
        {
            return await files.WriteAsync(PathFor(identity), JsonSerializer.SerializeToUtf8Bytes(value, Options), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<Unit>.Fail(ProblemCodes.StoreWrite, ex.Message); }
    }

    public Result<string> Quarantine(string identity)
    {
        var path = PathFor(identity);
        try
        {
            if (!File.Exists(path))
                return Result<string>.Ok("");
            var moved = path + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss") + ".bad";
            File.Move(path, moved, true);
            return Result<string>.Ok(moved);
        }
        catch (Exception ex) { return Result<string>.Fail(ProblemCodes.StoreWrite, ex.Message); }
    }
}
