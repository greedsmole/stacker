using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    internal ContentAvailability? ContentStatus(string scope)
    {
        using var c = Open();
        using var command = Command(c, null, """
            SELECT availability
              FROM object_catalog
             WHERE scope=$s
            """, ("$s", scope));
        return command.ExecuteScalar() is long value ? (ContentAvailability)value : null;
    }
    internal void RecordContents(string scope, ContentAvailability availability)
    {
        using var c = Open();
        using var command = Command(c, null, """
              INSERT INTO object_catalog(
              scope,
              availability
            )
                   VALUES (
              $s,
              $a
            )
              ON CONFLICT (scope)
            DO UPDATE SET availability=$a
            """, ("$s", scope), ("$a", (int)availability));
        command.ExecuteNonQuery();
    }
}
