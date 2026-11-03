using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed class WorkspaceBackupService(IApplicationState state,SqliteHistoryStore database,IObjectStore objects,IProcessLauncher launcher):IWorkspaceBackupService
{
    private sealed record Manifest(int Schema,DateTimeOffset CreatedAt,Dictionary<string,string> Files);
    private static readonly string[] Folders=["history-objects","settings","journal","workspace","workspace-index","workspace-stack","workspace-stacks","snapshots","repositories","drafts"];
    public Task<Result<string>> ExportAsync(string destination,CancellationToken ct=default)=>Boundary.CaptureAsync(async()=>
    {
        ProtectProfile(destination);
        var staging=Directory.CreateTempSubdirectory("stacker-backup-").FullName;var leases=new List<ObjectSnapshotLease>();var temporary=destination+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            var history=Path.Combine(state.Root,"history-objects");
            foreach(var root in Directory.Exists(history)?Directory.GetDirectories(history):[])
                leases.Add((await objects.AcquireAsync(root,false,ct)).OrThrow());
            using(var source=database.Open())using(var backup=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(staging,"review-history.db")))
            {backup.Open();source.BackupDatabase(backup);}
            foreach(var folder in Folders)
            {
                var source=Path.Combine(state.Root,folder);if(!Directory.Exists(source))continue;
                foreach(var file in Directory.EnumerateFiles(source,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}))
                {
                    ct.ThrowIfCancellationRequested();var target=Path.Combine(staging,Path.GetRelativePath(state.Root,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);
                }
            }
            var hashes=new Dictionary<string,string>();
            foreach(var file in Directory.EnumerateFiles(staging,"*",SearchOption.AllDirectories))
                hashes[Path.GetRelativePath(staging,file).Replace('\\','/')]=await HashAsync(file,ct);
            await File.WriteAllTextAsync(Path.Combine(staging,"manifest.json"),JsonSerializer.Serialize(new Manifest(1,DateTimeOffset.UtcNow,hashes)),ct);
            await Task.Run(()=>ZipFile.CreateFromDirectory(staging,temporary,CompressionLevel.Fastest,false),ct);
            File.Move(temporary,destination,true);return destination;
        }
        finally{foreach(var lease in leases)lease.Dispose();Directory.Delete(staging,true);if(File.Exists(temporary))File.Delete(temporary);}
    },ProblemCodes.StoreWrite);
    public Task<Result<string>> RestoreAsync(string archive,string emptyDestination,CancellationToken ct=default)=>Boundary.CaptureAsync(async()=>
    {
        if(Directory.Exists(emptyDestination)&&Directory.EnumerateFileSystemEntries(emptyDestination).Any())throw new StackerException("Restore requires an empty folder. Existing workspace data was preserved.");
        var staging=emptyDestination+".restore-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(staging);
        try
        {
            using var zip=ZipFile.OpenRead(archive);
            var manifestEntry=zip.GetEntry("manifest.json")??throw new StackerException("Backup manifest is missing.");
            using var manifestStream=manifestEntry.Open();var manifest=await JsonSerializer.DeserializeAsync<Manifest>(manifestStream,cancellationToken:ct)??throw new StackerException("Backup manifest is unreadable.");
            if(manifest.Schema!=1)throw new StackerException("Unsupported backup schema.");
            if(!manifest.Files.ContainsKey("review-history.db"))throw new StackerException("Backup database is missing from the manifest.");
            foreach(var(path,hash)in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();var target=Path.GetFullPath(Path.Combine(staging,path));
                if(!target.StartsWith(Path.GetFullPath(staging)+Path.DirectorySeparatorChar,StringComparison.Ordinal))throw new StackerException("Backup contains an invalid file path.");
                var entry=zip.GetEntry(path)??throw new StackerException("Backup file is missing: "+path);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using(var input=entry.Open())using(var output=File.Create(target))await input.CopyToAsync(output,ct);
                if(await HashAsync(target,ct)!=hash)throw new StackerException("Backup checksum mismatch: "+path);
            }
            using(var verify=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(staging,"review-history.db")))
            {
                verify.Open();using var check=verify.CreateCommand();check.CommandText="PRAGMA integrity_check";
                if(check.ExecuteScalar() as string!="ok")throw new StackerException("Restored database integrity check failed.");
                check.CommandText="PRAGMA foreign_key_check";if(check.ExecuteScalar() is not null)throw new StackerException("Restored history has broken references.");
                check.CommandText="PRAGMA user_version";var schema=Convert.ToInt32(check.ExecuteScalar());
                if(schema<1||schema>SqliteHistoryStore.Schema)throw new StackerException("Backup uses an unsupported history schema.");
            }
            if(Directory.Exists(emptyDestination))Directory.Delete(emptyDestination);Directory.Move(staging,emptyDestination);return emptyDestination;
        }
        finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
    },ProblemCodes.StoreRead);
    public Task<Result<Unit>> SaveRecoveryTextAsync(string destination,string text,CancellationToken ct=default)=>Boundary.CaptureAsync(()=>
    {ProtectProfile(destination);return File.WriteAllTextAsync(destination,text,ct);},ProblemCodes.StoreWrite);
    private void ProtectProfile(string destination)
    {
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        var root=Path.GetFullPath(state.Root);
        if(Path.GetFullPath(destination).StartsWith(root+Path.DirectorySeparatorChar,comparison))throw new StackerException("Choose an export location outside the active workspace profile.");
    }
    private static async Task<string> HashAsync(string path,CancellationToken ct)
    {await using var stream=File.OpenRead(path);return Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));}
    public Result<Unit> OpenRestoredWorkspace(string root)=>Boundary.Capture(()=>
    {
        var executable=Environment.ProcessPath??throw new StackerException("Cannot locate the application executable.");
        string[] args=Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase)?[Path.Combine(AppContext.BaseDirectory,"Stacker.dll"),"--data-root",root]:["--data-root",root];
        launcher.Start(new(executable,args)).OrThrow();
    },ProblemCodes.GitFailed);
}
