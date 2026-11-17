using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
namespace Stacker.Infrastructure;

public sealed class RoutingJournal(IJournal local, SqliteHistoryStore database, IClock clock) : IJournal
{
    private readonly IJournal _remote = new JsonJournal(new SqliteJournalState(database, local), clock);
    private IJournal For(string scope) => scope.StartsWith("github:", StringComparison.Ordinal) ? _remote : local;
    public Task<Result<JournalEntry>> OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct = default) => For(repository).OpenAsync(repository, kind, planned, ct);
    public Task<Result<JournalEntry>> StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct = default) => For(entry.Repository).StepAsync(entry, step, ct);
    public Task<Result<JournalEntry>> CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef = null, string? pendingStepId = null, string? message = null, CancellationToken ct = default) => For(entry.Repository).CloseAsync(entry, phase, stashRef, pendingStepId, message, ct);
    public Task<Result<JournalEntry>> SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct = default) => For(entry.Repository).SetStashAsync(entry, stashRef, ct);
    public Task<Result<JournalEntry?>> ActiveAsync(string repository, CancellationToken ct = default) => For(repository).ActiveAsync(repository, ct);
    public Task<Result<JournalEntry?>> UndoableAsync(string repository, CancellationToken ct = default) => For(repository).UndoableAsync(repository, ct);
    public Task<Result<JournalEntry?>> ByIdAsync(string repository, string id, CancellationToken ct = default) => For(repository).ByIdAsync(repository, id, ct);
    public Result<IReadOnlyList<JournalEntry>> Recent(string repository, int count = 20) => For(repository).Recent(repository, count);
    public Task<Result<IReadOnlyList<SavedStash>>> StashesAsync(string repository, CancellationToken ct = default) => For(repository).StashesAsync(repository, ct);
}
