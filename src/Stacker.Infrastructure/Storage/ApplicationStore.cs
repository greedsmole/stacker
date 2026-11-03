// Purpose: Stores application-owned snapshots, preferences, and drafts under stable hashed identities outside the user repository.
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
namespace Stacker.Infrastructure;

/// <summary>Application data keyed by identity; reads never throw, damaged documents are quarantined.</summary>
public sealed class ApplicationStore : IApplicationState
{
    public string Root { get; }
    private readonly IAtomicFile _files;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ApplicationStore(string? root = null, IAtomicFile? files = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stacker", "v3");
        _files = files ?? new AtomicFile();
        ImportLegacy(Root);
    }
    /// <summary>Set when a damaged document was moved aside, so the UI can mention it once.</summary>
    public string? LastProblem { get; private set; }

    public static string Key(string identity) => FileLockFactory.Hash(identity);

    /// <summary>Brings forward data written by the previous layout, once, without deleting the original.</summary>
    private void ImportLegacy(string root)
    {
        try
        {
            var legacy = Path.Combine(Path.GetDirectoryName(root)!, "v2");
            var marker = Path.Combine(root, ".imported-v2");
            var oldSettings = Path.Combine(Path.GetDirectoryName(root)!, "settings.json");
            if (File.Exists(marker) || Path.GetFileName(root) != "v3" || (!Directory.Exists(legacy) && !File.Exists(oldSettings))) return;
            Directory.CreateDirectory(root);
            foreach (var file in Directory.Exists(legacy) ? Directory.EnumerateFiles(legacy, "*.json", SearchOption.AllDirectories) : [])
            {
                var target = Path.Combine(root, Path.GetRelativePath(legacy, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(file, target, false);
            }
            var settingsTarget = Path.Combine(root, "settings", "settings.json");
            if (File.Exists(oldSettings) && !File.Exists(settingsTarget))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsTarget)!);
                File.Copy(oldSettings, settingsTarget, false);
            }
            File.WriteAllText(marker, "complete");
        }
        catch (Exception ex) { LastProblem = "Legacy import is incomplete and will be retried: " + ex.Message; }
    }

    public async Task<T?> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class
    {
        await Task.CompletedTask;
        var result = Store<T>(kind).Read(identity);
        if (result.IsFailed) LastProblem = result.Problem!.Message;
        return result.Value;
    }

    public async Task WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class
    {
        await _gate.WaitAsync(ct);
        try
        {
            var result = await Store<T>(kind).WriteAsync(identity, value, ct);
            if (result.IsFailed) { LastProblem = result.Problem!.Message; result.OrThrow(); }
        }
        finally { _gate.Release(); }
    }

    Task<Result<T?>> IApplicationState.ReadAsync<T>(string kind, string identity, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        var result = Store<T>(kind).Read(identity);
        if (result.IsFailed) LastProblem = result.Problem!.Message;
        return Task.FromResult(result);
    }
    async Task<Result<Unit>> IApplicationState.WriteAsync<T>(string kind, string identity, T value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var result = await Store<T>(kind).WriteAsync(identity, value, ct);
            if (result.IsFailed) LastProblem = result.Problem!.Message;
            return result;
        }
        finally { _gate.Release(); }
    }

    private JsonStateStore<T> Store<T>(string kind) where T : class => new(_files, Path.Combine(Root, kind));
}
