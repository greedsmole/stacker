using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    internal ContentAvailability? RepositoryContentStatus(RepositoryIdentity repository,string scope)
    {
        using var c=Open();using var command=Command(c,null,"SELECT availability FROM repository_contents WHERE repository_id=$r AND scope=$s",("$r",repository.Repository.Value),("$s",scope));
        return command.ExecuteScalar() is long value?(ContentAvailability)value:null;
    }
    internal void RecordRepositoryContents(RepositoryIdentity repository,string scope,ContentAvailability availability)
    {
        using var c=Open();using var command=Command(c,null,"INSERT INTO repository_contents(repository_id,scope,availability) VALUES($r,$s,$a) ON CONFLICT(repository_id,scope) DO UPDATE SET availability=$a",("$r",repository.Repository.Value),("$s",scope),("$a",(int)availability));command.ExecuteNonQuery();
    }
    internal bool HistoryRemoved(RepositoryIdentity repository,string scope)=>
        (RepositoryContentStatus(repository,scope)??ContentStatus(scope))==ContentAvailability.Removed;
    internal HashSet<string> RemovedScopeKeys(RepositoryIdentity repository)
    {
        using var c=Open();using var query=Command(c,null,"SELECT scope FROM repository_contents WHERE repository_id=$r AND availability=2 UNION SELECT scope FROM object_catalog old WHERE availability=2 AND NOT EXISTS(SELECT 1 FROM repository_contents current WHERE current.repository_id=$r AND current.scope=old.scope)",("$r",repository.Repository.Value));
        var result=new HashSet<string>();using var reader=query.ExecuteReader();while(reader.Read())result.Add(RepositoryGit.ScopeKey(reader.GetString(0)));return result;
    }
    internal bool WasImported(RepositoryIdentity repository,string source)
    {
        using var c=Open();using var command=Command(c,null,"SELECT 1 FROM object_imports WHERE repository_id=$r AND source=$s",("$r",repository.Repository.Value),("$s",source));return command.ExecuteScalar() is not null;
    }
    internal void MarkImported(RepositoryIdentity repository,string source)
    {
        using var c=Open();using var command=Command(c,null,"INSERT OR IGNORE INTO object_imports(repository_id,source) VALUES($r,$s)",("$r",repository.Repository.Value),("$s",source));command.ExecuteNonQuery();
    }
    internal IReadOnlyList<string> ObjectScopes(RepositoryIdentity repository,string scope)
    {
        var canonical=ObjectScope(scope);var scopes=new HashSet<string>{scope,canonical};
        using var c=Open();using var binding=Command(c,null,"SELECT 1 FROM change_bindings WHERE local_scope=$l AND remote_scope=$r LIMIT 1",("$l",repository.Scope),("$r",canonical));
        if(canonical==repository.Scope||scope==repository.Scope||binding.ExecuteScalar() is not null)
        {
            scopes.Add(repository.Scope);scopes.Add("local:"+repository.Root);
            using var aliases=Command(c,null,"SELECT alias FROM scope_aliases WHERE scope=$s",("$s",repository.Scope));using var rows=aliases.ExecuteReader();while(rows.Read())scopes.Add(rows.GetString(0));
        }
        return scopes.ToArray();
    }
    internal IReadOnlyList<string> LegacySources(RepositoryIdentity repository,string scope,string root)
    {
        var scopes=new HashSet<string>{scope,ObjectScope(scope)};
        using var bindingConnection=Open();using var binding=Command(bindingConnection,null,"SELECT 1 FROM change_bindings WHERE local_scope=$l AND remote_scope=$r LIMIT 1",("$l",repository.Scope),("$r",scope));
        if(scope.StartsWith("local:",StringComparison.Ordinal)||binding.ExecuteScalar() is not null)
        {
            scopes.Add(repository.Scope);scopes.Add("local:"+repository.Root);
            using var c=Open();using var query=Command(c,null,"SELECT alias FROM scope_aliases WHERE scope=$s",("$s",repository.Scope));using var reader=query.ExecuteReader();while(reader.Read())scopes.Add(reader.GetString(0));
        }
        return scopes.SelectMany(s=>new[]{Path.Combine(root,"history-objects",ApplicationStore.Key(s)),Path.Combine(root,"objects",ApplicationStore.Key(s))}).Append(Path.Combine(root,"repository-bundles",repository.Repository.Value+".bundle")).Distinct().ToArray();
    }
}
