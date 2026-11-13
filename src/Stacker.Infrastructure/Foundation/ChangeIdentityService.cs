using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed class ChangeIdentityService(SqliteHistoryStore store, IProcessRunner runner, GitExecutable git) : IChangeIdentityService
{
    public Task<Result<IReadOnlyList<RepositoryIdentity>>> KnownAsync(CancellationToken ct = default) => Boundary.CaptureAsync<IReadOnlyList<RepositoryIdentity>>(() => Task.Run<IReadOnlyList<RepositoryIdentity>>(() =>
    {
        using var c = store.Open();
        using var command = c.CreateCommand();
        command.CommandText = """
              SELECT r.id,
                     l.worktree_id,
                     l.root,
                     r.common_directory
                FROM locations l
                JOIN repositories r
                  ON r.id=l.repository_id
            ORDER BY l.root
            """;
        var result = new List<RepositoryIdentity>();
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            result.Add(new(new(rows.GetString(0)), new(rows.GetString(1)), rows.GetString(2), rows.GetString(3)));
        }

        return result;
    }, ct), ProblemCodes.StoreRead);
    public Task<Result<RepositoryIdentity>> RegisterAsync(string root, CancellationToken ct = default) => Register(root, null, null, ct);
    public Task<Result<RepositoryIdentity>> RelocateAsync(RepositoryId repository, string root, WorktreeId? worktree = null, CancellationToken ct = default) => Register(root, repository.Value, worktree?.Value, ct);
    private Task<Result<RepositoryIdentity>> Register(string root, string? requested, string? requestedWorktree, CancellationToken ct) => Boundary.CaptureAsync(async () =>
    {
        var result = (await runner.RunAsync(new(git.Resolve(), ["rev-parse", "--path-format=absolute", "--git-common-dir", "--show-toplevel"], root), ct)).OrThrow();
        if (result.ExitCode != 0)
        {
            throw new StackerException(result.StdErr);
        }

        var paths = result.StdOut.TrimEnd('\r', '\n').Split('\n');
        if (paths.Length != 2)
        {
            throw new StackerException("Git did not resolve the repository location unambiguously.");
        }

        var common = Path.GetFullPath(paths[0].TrimEnd('\r'));
        var location = Path.GetFullPath(paths[1].TrimEnd('\r'));
        var identity = await Task.Run(() =>
        {
            using var c = store.Open();
            using var tx = c.BeginTransaction();
            string? Scalar(string sql, params (string, object?)[] values)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                foreach (var (k, v) in values)
                {
                    cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
                }

                return cmd.ExecuteScalar() as string;
            }

            void Execute(string sql, params (string, object?)[] values)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                foreach (var (k, v) in values)
                {
                    cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
                }

                cmd.ExecuteNonQuery();
            }

            var known = Scalar("""
                SELECT id
                  FROM repositories
                 WHERE common_directory=$c
                """, ("$c", common));
            if (requested is not null && known is not null && known != requested)
            {
                throw new StackerException("This repository already has a different identity. Histories were preserved.");
            }

            var id = requested ?? known ?? Guid.NewGuid().ToString("N");
            if (requested is not null && Scalar("""
                SELECT id
                  FROM repositories
                 WHERE id=$i
                """, ("$i", id)) is null)
            {
                throw new StackerException("The original repository identity is missing.");
            }

            if (requestedWorktree is not null && Scalar("""
                SELECT worktree_id
                  FROM locations
                 WHERE repository_id=$i
                   AND worktree_id=$w
                """, ("$i", id), ("$w", requestedWorktree)) is null)
            {
                throw new StackerException("The original worktree identity is missing.");
            }

            Execute("""
                  INSERT INTO repositories(
                  id,
                  common_directory
                )
                       VALUES (
                  $i,
                  $c
                )
                  ON CONFLICT (id)
                DO UPDATE SET common_directory=$c
                """, ("$i", id), ("$c", common));
            var knownWorktree = Scalar("""
                SELECT worktree_id
                  FROM locations
                 WHERE root=$r
                   AND repository_id=$i
                """, ("$r", location), ("$i", id));
            if (requestedWorktree is not null && knownWorktree is not null && knownWorktree != requestedWorktree)
            {
                throw new StackerException("This location belongs to a different worktree. Its identity was preserved.");
            }

            var worktree = requestedWorktree ?? knownWorktree ?? Guid.NewGuid().ToString("N");
            Execute("""
                  INSERT INTO locations(
                  root,
                  repository_id,
                  worktree_id
                )
                       VALUES (
                  $r,
                  $i,
                  $w
                )
                  ON CONFLICT (root)
                DO UPDATE SET repository_id=$i,
                              worktree_id=$w
                """, ("$r", location), ("$i", id), ("$w", worktree));
            Execute("""
                  INSERT INTO scope_aliases(
                  alias,
                  scope
                )
                       VALUES (
                  $a,
                  $s
                )
                  ON CONFLICT (alias)
                DO UPDATE SET scope=$s
                """, ("$a", "local:" + location), ("$s", "local:" + id));
            tx.Commit();
            store.MigrateScope("local:" + location, "local:" + id);
            return new RepositoryIdentity(new(id), new(worktree), location, common);
        }, ct);
        return identity;
    }, ProblemCodes.StoreWrite);
    public Task<Result<Unit>> BindAsync(string localScope, string localRef, GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default) =>
        Boundary.CaptureAsync(async () =>
        {
            if (pr.HeadRepositoryId != context.RepositoryId || localRef != "refs/heads/" + pr.HeadRef)
            {
                throw new StackerException("Local ref and PR head do not identify the same branch.");
            }

            var local = (await store.FindNodeAsync(localScope, localRef, ct: ct)).OrThrow();
            var remote = (await store.ResolveNodeAsync(context.Identity, pr.HeadRef, pr.Title, pr.Number, ct)).OrThrow();
            using var c = store.Open();
            using var tx = c.BeginTransaction();
            using (var ownership = c.CreateCommand())
            {
                ownership.Transaction = tx;
                ownership.CommandText = """
                    SELECT remote_scope
                      FROM change_bindings
                     WHERE local_scope=$s
                       AND remote_scope!=$r
                     LIMIT 1
                    """;
                ownership.Parameters.AddWithValue("$s", localScope);
                ownership.Parameters.AddWithValue("$r", context.Identity);
                if (ownership.ExecuteScalar() is string)
                {
                    throw new StackerException("This local review is bound to another account. Account histories remain separate.");
                }
            }

            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                  INSERT INTO change_bindings(
                  local_scope,
                  local_ref,
                  remote_scope,
                  pr
                )
                       VALUES (
                  $l,
                  $f,
                  $r,
                  $p
                )
                  ON CONFLICT (
                  local_scope,
                  local_ref,
                  remote_scope
                )
                DO UPDATE SET pr=$p
                """;
            cmd.Parameters.AddWithValue("$l", localScope);
            cmd.Parameters.AddWithValue("$f", localRef);
            cmd.Parameters.AddWithValue("$r", context.Identity);
            cmd.Parameters.AddWithValue("$p", pr.Number);
            await cmd.ExecuteNonQueryAsync(ct);
            if (local is not null)
            {
                using var alias = c.CreateCommand();
                alias.Transaction = tx;
                alias.CommandText = """
                      INSERT INTO change_aliases(
                      alias_id,
                      canonical_id
                    )
                           VALUES (
                      $a,
                      $c
                    )
                      ON CONFLICT (alias_id)
                    DO UPDATE SET canonical_id=$c
                    """;
                alias.Parameters.AddWithValue("$a", local.Id);
                alias.Parameters.AddWithValue("$c", remote.Id);
                await alias.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }, ProblemCodes.StoreWrite);
}
