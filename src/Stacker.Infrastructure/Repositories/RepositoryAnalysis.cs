using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed partial class RepositoryGit
{
    internal async Task<ProcessResult> AnalysisAsync(RepositoryIdentity repository,string[] args,CancellationToken ct)
    {
        var configured=await RunAsync(repository,["config","--name-only","--get-regexp",@"^merge\..*\.driver$"],ct,true);
        if(configured.ExitCode is not(0 or 1))throw new StackerException("Cannot inspect configured merge drivers: "+configured.StdErr.Trim());
        var drivers=configured.StdOut.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        var options=drivers.SelectMany(key=>new[]{"-c",key+"=false"}).ToArray();
        var result=await RunAsync(repository,["-c","merge.renormalize=false","-c","rerere.enabled=false",..options,..args],ct,true);
        if(drivers.Length>0&&result.ExitCode==1)throw new StackerException("Textual merge preview is unavailable when a configured external merge driver may be required. Inspect the original diffs.");
        return result;
    }
}
