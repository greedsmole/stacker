// Purpose: Repository YAML persistence with revision checks and atomic replacement, plus per-user JSON preferences.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Stacker.Infrastructure;

public sealed class YamlStackStore(IAtomicFile? files = null, IFileLockFactory? locks = null) : IStackStore
{
    public const string FileName = ".stackpr.yml";
    private readonly IAtomicFile _files = files ?? new AtomicFile();
    // Locks live in application data: a crashed process can never leave a marker file inside the user's repository.
    private readonly IFileLockFactory _locks = locks ?? new FileLockFactory(DefaultLockDirectory());
    private readonly IDeserializer _reader = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).WithDuplicateKeyChecking().Build();
    private readonly ISerializer _writer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public static string DefaultLockDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stacker", "locks");

    public Task<StackDocument> LoadAsync(string root, CancellationToken ct = default)
    {
        var path = System.IO.Path.Combine(root, FileName);
        _files.SweepTemporaries(path);
        var bytes = _files.Read(path);
        if (bytes.IsFailed) throw new StackerException(bytes.Problem!.Message);
        if (bytes.Value is null) return Task.FromResult(new StackDocument([], null));
        try
        {
            var data = _reader.Deserialize<YamlDocument>(Encoding.UTF8.GetString(bytes.Value));
            if (data is null || data.Version != 1 || data.Stacks is null) throw new StackerException("Unsupported stack configuration. Expected version: 1 and a stacks list.");
            var stacks = data.Stacks.Select(s => new StackDefinition(s.Id ?? "", s.Name ?? "", s.Base ?? "", s.Branches ?? throw new StackerException("Each stack requires a branches list."))).ToArray();
            StackValidation.Validate(stacks);
            return Task.FromResult(new StackDocument(stacks, Hash(bytes.Value)));
        }
        catch (YamlDotNet.Core.YamlException ex) { throw new StackerException($"Cannot read {FileName}. The file was not changed. {ex.Message}"); }
    }

    public async Task<StackDocument> SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct = default)
    {
        StackValidation.Validate(stacks);
        var path = System.IO.Path.Combine(root, FileName);
        // The content hash detects edits made by other tools; this lock only coordinates Stacker windows.
        var held = await _locks.TryAcquireAsync(root, ct);
        if (held.IsFailed) throw new StackerException(held.Problem!.Message);
        await using var handle = held.Value!;
        var current = await LoadAsync(root, ct);
        // An absent file cannot hold someone else's edit, so a branch that does not carry the definition yet can still be saved.
        if (current.Revision is not null && current.Revision != expectedRevision)
            throw new StackerException($"{FileName} changed outside this window. Refresh to reload it before saving.");
        var data = new YamlDocument { Version = 1, Stacks = stacks.Select(s => new YamlStack { Id = s.Id, Name = s.Name, Base = s.Base, Branches = s.Branches.ToList() }).ToList() };
        var bytes = Encoding.UTF8.GetBytes(_writer.Serialize(data));
        var revision = File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;
        if (revision is not null && revision != expectedRevision) throw new StackerException($"{FileName} changed during saving. Refresh before retrying.");
        ct.ThrowIfCancellationRequested();
        var write = await _files.WriteAsync(path, bytes, ct);
        if (write.IsFailed) throw new StackerException(write.Problem!.Message);
        return new(stacks.ToArray(), Hash(bytes));
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

/// <summary>Per-user preferences; a damaged file is moved aside and defaults are used instead.</summary>
public sealed class JsonSettingsStore(string? directory = null, IAtomicFile? files = null) : ISettingsStore
{
    private readonly IAtomicFile _files = files ?? new AtomicFile();
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stacker");
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Set when a damaged settings file was quarantined, so the UI can say so once.</summary>
    public string? LastProblem { get; private set; }
    private string PathFor => Path.Combine(_directory, "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        _files.SweepTemporaries(PathFor);
        var bytes = _files.Read(PathFor);
        if (bytes.IsFailed) { LastProblem = bytes.Problem!.Message; return new(); }
        if (bytes.Value is null) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(Encoding.UTF8.GetString(bytes.Value)) ?? new();
            settings.RecentRepositories ??= [];
            settings.Theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "Dark";
            settings.NavigationWidth = Clamp(settings.NavigationWidth, 200, 500, 270);
            settings.FilesWidth = Clamp(settings.FilesWidth, 200, 600, 290);
            settings.WindowWidth = Clamp(settings.WindowWidth, 1000, 4000, 1400);
            settings.WindowHeight = Clamp(settings.WindowHeight, 640, 2500, 880);
            settings.SchemaVersion = AppSettings.CurrentSchema;
            return settings;
        }
        catch (JsonException ex)
        {
            var moved = PathFor + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss") + ".bad";
            try { File.Move(PathFor, moved, true); } catch (IOException) { }
            LastProblem = $"settings.json was damaged and has been reset. A copy was kept at {Path.GetFileName(moved)}. ({ex.Message})";
            return new();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            settings.SchemaVersion = AppSettings.CurrentSchema;
            var write = await _files.WriteAsync(PathFor, JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true }), ct);
            if (write.IsFailed) LastProblem = write.Problem!.Message;
        }
        finally { _gate.Release(); }
    }

    private static double Clamp(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
