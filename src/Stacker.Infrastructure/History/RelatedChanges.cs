using Microsoft.Data.Sqlite;
using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    private static string Canonical(SqliteConnection c, string change)
    {
        var seen = new HashSet<string>();
        while (seen.Add(change))
        { using var query = Command(c, null, "SELECT canonical_id FROM change_aliases WHERE alias_id=$i", ("$i", change)); if (query.ExecuteScalar() is not string next) return change; change = next; }
        throw new StackerException("Change identity aliases contain a cycle.");
    }
    private static IReadOnlyList<string> Related(SqliteConnection c, string change)
    {
        var canonical = Canonical(c, change);
        using var query = Command(c, null, "WITH RECURSIVE related(id) AS (SELECT $i UNION SELECT a.alias_id FROM change_aliases a JOIN related r ON a.canonical_id=r.id) SELECT id FROM related", ("$i", canonical));
        var ids = new List<string>(); using var reader = query.ExecuteReader(); while (reader.Read()) ids.Add(reader.GetString(0)); return ids;
    }
    private static IReadOnlyList<T> RelatedRecords<T>(SqliteConnection c, string kind, string change)
    {
        var records = new List<T>(); foreach (var id in Related(c, change)) records.AddRange(All<T>(c, kind, "ChangeId", id)); return records;
    }
    internal string ObjectScope(string scope)
    {
        using var c = Open(); using var command = Command(c, null, "SELECT DISTINCT remote_scope FROM change_bindings WHERE local_scope=$s", ("$s", scope));
        using var reader = command.ExecuteReader(); return reader.Read() ? reader.GetString(0) : scope;
    }
}
