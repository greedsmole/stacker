using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Stacker.Infrastructure;

public sealed class YamlStackStore : IStackStore
{
    private readonly IDeserializer _reader = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).WithDuplicateKeyChecking().Build();
    private readonly ISerializer _writer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
    public async Task<StackDocument> LoadAsync(string root, CancellationToken ct = default)
    {
        var path = Path.Combine(root, ".stackpr.yml");
        if (!File.Exists(path)) return new([], null);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        try
        {
            var data = _reader.Deserialize<YamlDocument>(Encoding.UTF8.GetString(bytes));
            if (data is null || data.Version != 1 || data.Stacks is null) throw new StackerException("Unsupported stack configuration. Expected version: 1 and a stacks list.");
            var stacks = data.Stacks.Select(s => new StackDefinition(s.Id ?? "", s.Name ?? "", s.Base ?? "", s.Branches ?? throw new StackerException("Each stack requires a branches list."))).ToArray();
            StackValidation.Validate(stacks);
            return new(stacks, Hash(bytes));
        }
        catch (YamlDotNet.Core.YamlException ex) { throw new StackerException($"Cannot read .stackpr.yml. The file was not changed. {ex.Message}"); }
    }
    public async Task<StackDocument> SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct = default)
    {
        StackValidation.Validate(stacks);
        var path = Path.Combine(root, ".stackpr.yml");
        // Coordinate Stacker writers; the content hash also detects edits made by other tools.
        var lockPath = path + ".lock";
        FileStream writeLock;
        try { writeLock = new(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { throw new StackerException("Stack configuration is being saved by another process. Retry after it finishes."); }
        await using (writeLock)
        {
            var current = await LoadAsync(root, ct);
            if (current.Revision != expectedRevision) throw new StackerException(".stackpr.yml changed outside this window. Refresh to reload it before saving.");
            var data = new YamlDocument { Version = 1, Stacks = stacks.Select(s => new YamlStack { Id = s.Id, Name = s.Name, Base = s.Base, Branches = s.Branches.ToList() }).ToList() };
            var bytes = Encoding.UTF8.GetBytes(_writer.Serialize(data));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, ct);
                var revision = File.Exists(path) ? Hash(await File.ReadAllBytesAsync(path, ct)) : null;
                if (revision != expectedRevision) throw new StackerException(".stackpr.yml changed during saving. Refresh before retrying.");
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new(stacks.ToArray(), Hash(bytes));
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public sealed class YamlDocument
    {
        public int Version { get; set; }
        public List<YamlStack>? Stacks { get; set; }
    }
    public sealed class YamlStack
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Base { get; set; }
        public List<string>? Branches { get; set; }
    }
}

public sealed class JsonSettingsStore(string? directory = null) : ISettingsStore
{
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stacker");
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_directory, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path, ct)) ?? new();
            settings.RecentRepositories ??= [];
            settings.NavigationWidth = Clamp(settings.NavigationWidth, 200, 500, 270);
            settings.FilesWidth = Clamp(settings.FilesWidth, 200, 600, 290);
            settings.WindowWidth = Clamp(settings.WindowWidth, 1000, 4000, 1400);
            settings.WindowHeight = Clamp(settings.WindowHeight, 640, 2500, 880);
            return settings;
        }
        catch (JsonException) { return new(); }
    }
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "settings.json");
            var temp = path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), ct);
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _gate.Release(); }
    }
    private static double Clamp(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
