using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed partial class WorkspaceBackupService
{
    private sealed record Manifest(int Schema, DateTimeOffset CreatedAt, Dictionary<string, string> Files);
}
