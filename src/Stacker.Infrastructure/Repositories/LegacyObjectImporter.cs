using Stacker.Core;
namespace Stacker.Infrastructure;

/// <summary>Imports old application-owned caches from the opened repository's working directory.</summary>
public sealed class LegacyObjectImporter(RepositoryGit git,IApplicationState state,SqliteHistoryStore database)
{
    internal async Task ImportAsync(RepositoryIdentity repository,string scope,CancellationToken ct)
    {
        if(database.HistoryRemoved(repository,scope))return;
        var removed=database.RemovedScopeKeys(repository);
        foreach(var source in database.LegacySources(repository,scope,state.Root))
        {
            ct.ThrowIfCancellationRequested();
            var bundle=source.EndsWith(".bundle",StringComparison.Ordinal);
            if(!(bundle?File.Exists(source):Directory.Exists(source))||database.WasImported(repository,source))continue;
            var listed=await git.RunAsync(repository,bundle?["bundle","list-heads",source]:["-c","protocol.file.allow=always","ls-remote","--refs","--",source],ct);
            var refs=listed.StdOut.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>line.Split(bundle?' ':'\t'))
                .Where(parts=>parts.Length==2&&parts[1].StartsWith("refs/",StringComparison.Ordinal))
                .Where(parts=>!bundle||!parts[1].StartsWith("refs/stacker/history/",StringComparison.Ordinal)||(parts[1].Split('/').Length<6||!removed.Contains(parts[1].Split('/')[4]))).DistinctBy(parts=>bundle?parts[1]:parts[0]).ToArray();
            var prefix=RepositoryGit.Prefix(repository,"history",scope);
            foreach(var item in refs)RepositoryGit.Sha(item[0]);
            if(refs.Length>0)
            {
                var refspecs=refs.Select(item=>"+"+item[1]+":"+(bundle&&item[1].StartsWith($"refs/stacker/history/{repository.Repository.Value}/",StringComparison.Ordinal)?item[1]:prefix+item[0])).ToArray();
                await git.RunAsync(repository,["-c","protocol.file.allow=always","fetch","--atomic","--no-tags","--no-write-fetch-head","--no-recurse-submodules","--no-auto-maintenance","--stdin","--",source],ct,input:string.Join('\n',refspecs)+"\n");
                if((await git.MissingAsync(repository,refs.Select(item=>item[0]),ct)).Count>0)throw new StackerException("Historical import is incomplete. The original cache was preserved.");
            }
            database.MarkImported(repository,source);
        }
    }
}
