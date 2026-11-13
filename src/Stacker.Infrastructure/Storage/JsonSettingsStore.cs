using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Stacker.Infrastructure;

/// <summary>Per-user preferences; a damaged file is moved aside and defaults are used instead.</summary>
public sealed class JsonSettingsStore(string? directory = null, IAtomicFile? files = null) : ISettingsStore, ISettingsDiagnostics
{
    private readonly IAtomicFile _files = files ?? new AtomicFile();
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stacker");
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Set when a damaged settings file was quarantined, so the UI can say so once.</summary>
    public string? LastProblem
    {
        get; private set;
    }
    private string? _blockedWrite;
    private string PathFor => Path.Combine(_directory, "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        _blockedWrite = null;
        (_files.SweepTemporaries(PathFor)).OrThrow();
        var bytes = _files.Read(PathFor);
        if (bytes.IsFailed)
        {
            _blockedWrite = LastProblem = bytes.Problem!.Message;
            return new();
        }
        if (bytes.Value is null)
            return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(Encoding.UTF8.GetString(bytes.Value)) ?? new();
            if (settings.SchemaVersion > AppSettings.CurrentSchema)
            {
                _blockedWrite = "settings.json was saved by a newer Stacker version. It was preserved; use that version to read it.";
                throw new StackerException(_blockedWrite);
            }
            settings.RecentRepositories ??= [];
            settings.Theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "Dark";
            settings.DiffLineHeight = Math.Clamp(settings.DiffLineHeight, AppSettings.MinDiffLineHeight, AppSettings.MaxDiffLineHeight);
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
            try
            {
                File.Move(PathFor, moved, true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                _blockedWrite = LastProblem = "settings.json is damaged and could not be backed up. It was preserved: " + moveError.Message;
                return new();
            }
            LastProblem = $"settings.json was damaged and has been reset. A copy was kept at {Path.GetFileName(moved)}. ({ex.Message})";
            return new();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_blockedWrite is not null)
                throw new StackerException(_blockedWrite);
            settings.SchemaVersion = AppSettings.CurrentSchema;
            var write = await _files.WriteAsync(PathFor, JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true }), ct);
            if (write.IsFailed)
            {
                LastProblem = write.Problem!.Message;
                write.OrThrow();
            }
        }
        finally { _gate.Release(); }
    }

    private static double Clamp(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    Task<Result<AppSettings>> ISettingsStore.LoadAsync(CancellationToken ct) => Boundary.CaptureAsync(() => LoadAsync(ct), ProblemCodes.StoreRead);

    Task<Result<Unit>> ISettingsStore.SaveAsync(AppSettings settings, CancellationToken ct) => Boundary.CaptureAsync(() => SaveAsync(settings, ct), ProblemCodes.StoreWrite);
}
