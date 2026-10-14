// Purpose: Stores application-owned snapshots, preferences, and drafts under stable hashed identities outside the user repository.
using System.Text.Json;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
namespace Stacker.Infrastructure;

/// <summary>Application data keyed by identity; reads never throw, damaged documents are quarantined.</summary>
public sealed class ApplicationStore
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
    private static void ImportLegacy(string root)
    {
        try
        {
            var legacy = Path.Combine(Path.GetDirectoryName(root)!, "v2");
            if (Directory.Exists(root) || !Directory.Exists(legacy)) return;
            Directory.CreateDirectory(root);
            foreach (var file in Directory.EnumerateFiles(legacy, "*.json", SearchOption.AllDirectories))
            {
                var target = Path.Combine(root, Path.GetRelativePath(legacy, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, false);
            }
        }
        catch (Exception) { /* best effort: a failure leaves the previous data untouched */ }
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
            if (result.IsFailed) LastProblem = result.Problem!.Message;
        }
        finally { _gate.Release(); }
    }

    private JsonStateStore<T> Store<T>(string kind) where T : class => new(_files, Path.Combine(Root, kind));
}
