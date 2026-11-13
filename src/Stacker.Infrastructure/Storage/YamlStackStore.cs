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

public sealed partial class YamlStackStore(IAtomicFile? files = null, IFileLockFactory? locks = null) : IStackStore
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
        (_files.SweepTemporaries(path)).OrThrow();
        var bytes = _files.Read(path);
        if (bytes.IsFailed)
            throw new StackerException(bytes.Problem!.Message);
        if (bytes.Value is null)
            return Task.FromResult(new StackDocument([], null));
        try
        {
            var data = _reader.Deserialize<YamlDocument>(Encoding.UTF8.GetString(bytes.Value));
            if (data is null || data.Version != 1 || data.Stacks is null)
                throw new StackerException("Unsupported stack configuration. Expected version: 1 and a stacks list.");
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
        if (held.IsFailed)
            throw new StackerException(held.Problem!.Message);
        await using var handle = held.Value!;
        var current = await LoadAsync(root, ct);
        // An absent file cannot hold someone else's edit, so a branch that does not carry the definition yet can still be saved.
        if (current.Revision is not null && current.Revision != expectedRevision)
            throw new StackerException($"{FileName} changed outside this window. Refresh to reload it before saving.");
        var data = new YamlDocument { Version = 1, Stacks = stacks.Select(s => new YamlStack { Id = s.Id, Name = s.Name, Base = s.Base, Branches = s.Branches.ToList() }).ToList() };
        var bytes = Encoding.UTF8.GetBytes(_writer.Serialize(data));
        var previous = _files.Read(path).OrThrow();
        var revision = previous is null ? null : Hash(previous);
        if (revision is not null && revision != expectedRevision)
            throw new StackerException($"{FileName} changed during saving. Refresh before retrying.");
        ct.ThrowIfCancellationRequested();
        var write = await _files.WriteIfUnchangedAsync(path, bytes, previous, ct);
        if (write.IsFailed)
            throw new StackerException(write.Problem!.Message);
        return new(stacks.ToArray(), Hash(bytes));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    Task<Result<StackDocument>> IStackStore.LoadAsync(string root, CancellationToken ct) => Boundary.CaptureAsync(() => LoadAsync(root, ct), ProblemCodes.StoreRead);

    Task<Result<StackDocument>> IStackStore.SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct) => Boundary.CaptureAsync(() => SaveAsync(root, stacks, expectedRevision, ct), ProblemCodes.StoreWrite);
}
