using System.Collections.Concurrent;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
namespace Stacker.Infrastructure;

/// <summary>Runs repository object operations in the opened worktree; never creates or deletes a repository.</summary>
public sealed partial class RepositoryGit(IProcessRunner runner,GitExecutable executable)
{
    private readonly ConcurrentDictionary<string,SemaphoreSlim> _gates=new(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
    internal static string ScopeKey(string scope)=>ApplicationStore.Key(scope)[..32];
    internal static string Prefix(RepositoryIdentity repository,string kind,string scope)
    {
        if(repository.Repository.Value.Length==0||repository.Repository.Value.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('-' or '_')))throw new StackerException("Invalid repository identity.");
        return $"refs/stacker/{kind}/{repository.Repository.Value}/{ScopeKey(scope)}/";
    }
    internal static void Sha(string sha)
    {if(sha.Length is not(40 or 64)||!sha.All(Uri.IsHexDigit))throw new StackerException("Invalid commit object ID.");}
    internal async Task<IDisposable> LockAsync(RepositoryIdentity repository,CancellationToken ct)
    {
        Register(repository);var gate=_gates.GetOrAdd(Path.GetFullPath(repository.CommonDirectory),_=>new(1,1));await gate.WaitAsync(ct);return new Release(()=>gate.Release());
    }
    private sealed class Release(Action action):IDisposable
    {private Action? _action=action;public void Dispose()=>Interlocked.Exchange(ref _action,null)?.Invoke();}
    internal async Task<ProcessResult> RunAsync(RepositoryIdentity repository,string[] args,CancellationToken ct,bool allowFailure=false,string? input=null,TimeSpan? timeout=null)
    {
        Register(repository);
        var result=(await runner.RunAsync(new(executable.Resolve(),["--no-pager","-c","core.hooksPath="+Neutral.HooksPath,..args],repository.Root,
            timeout??TimeSpan.FromMinutes(2),StandardInput:input,Environment:new Dictionary<string,string>
            {["GIT_TERMINAL_PROMPT"]="0",["GIT_PAGER"]="cat",["GIT_NO_LAZY_FETCH"]="1",["GIT_OPTIONAL_LOCKS"]="0",["LC_ALL"]="C"},
            UnsetEnvironment:GitEnvironment.RepositoryRedirects),ct)).OrThrow();
        if(result.ExitCode!=0&&!allowFailure)throw new StackerException("Git: "+result.StdErr.Trim());return result;
    }
    internal async Task<IReadOnlyList<string>> MissingAsync(RepositoryIdentity repository,IEnumerable<string> commits,CancellationToken ct)
    {
        var shas=commits.Distinct().ToArray();foreach(var sha in shas)Sha(sha);if(shas.Length==0)return [];
        var response=await RunAsync(repository,["cat-file","--batch-check=%(objectname) %(objecttype)"],ct,true,string.Join('\n',shas)+"\n");
        if(response.ExitCode!=0)throw new StackerException("Cannot inspect repository objects: "+response.StdErr.Trim());
        var available=response.StdOut.Split('\n',StringSplitOptions.RemoveEmptyEntries).Where(line=>line.EndsWith(" commit",StringComparison.Ordinal)).Select(line=>line.Split(' ')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return shas.Where(sha=>!available.Contains(sha)).ToArray();
    }
    internal async Task PinAsync(RepositoryIdentity repository,string prefix,IEnumerable<string> commits,CancellationToken ct)
    {
        var refs=await RefsAsync(repository,prefix,ct);var commands=new List<string>();
        foreach(var sha in commits.Distinct())
        {
            Sha(sha);var reference=prefix+sha;
            if(refs.TryGetValue(reference,out var current)){if(current!=sha)throw new StackerException("A historical reference changed unexpectedly.");continue;}
            commands.Add("create "+reference+" "+sha);
        }
        if(commands.Count>0)await RunAsync(repository,["update-ref","--stdin"],ct,input:"start\n"+string.Join('\n',commands)+"\nprepare\ncommit\n");
    }
    internal async Task<Dictionary<string,string>> RefsAsync(RepositoryIdentity repository,string prefix,CancellationToken ct)
    {
        if(!prefix.StartsWith("refs/stacker/",StringComparison.Ordinal)||!prefix.EndsWith('/'))throw new StackerException("Expected an application-owned reference namespace.");
        var response=await RunAsync(repository,["for-each-ref","--format=%(refname) %(objectname)",prefix],ct);
        return response.StdOut.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(line=>line.Split(' ')).ToDictionary(parts=>parts[0],parts=>parts[1]);
    }
    internal async Task DeleteRefsAsync(RepositoryIdentity repository,string prefix,CancellationToken ct)
    {
        var refs=await RefsAsync(repository,prefix,ct);if(refs.Count==0)return;
        var input="start\n"+string.Concat(refs.Select(item=>"delete "+item.Key+" "+item.Value+"\n"))+"prepare\ncommit\n";
        await RunAsync(repository,["update-ref","--stdin"],ct,input:input);
    }
}
