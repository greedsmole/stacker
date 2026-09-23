using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Stacker.Infrastructure;

public sealed class ApplicationStore(string? root = null)
{
    public string Root { get; } = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stacker", "v2");
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string Key(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    public async Task<T?> ReadAsync<T>(string kind, string identity, CancellationToken ct = default)
    {
        var path = Path.Combine(Root, kind, Key(identity) + ".json");
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct));
    }
    public async Task WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var directory = Path.Combine(Root, kind); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Key(identity) + ".json"); var tmp = path + ".tmp";
            try { await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(value), ct); File.Move(tmp, path, true); }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        finally { _gate.Release(); }
    }
}
