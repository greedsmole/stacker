using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Tests;

public sealed partial class FrameworkTests
{
    private sealed class RecordingJournal : IJournal
    {
        public List<string> Calls { get; } = [];
        public JournalEntry? Last
        {
            get; private set;
        }
        public Task<JournalEntry> OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct = default)
        {
            Calls.Add("open");
            Last = new(JournalEntry.CurrentSchema, "e1", repository, kind, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, JournalPhase.Running, planned, null, null, null);
            return Task.FromResult(Last);
        }
        public Task<JournalEntry> StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct = default)
        {
            Calls.Add("step");
            Last = entry with
            {
                Steps = entry.Steps.Select(existing => existing.Id == step.Id ? step : existing).ToArray()
            };
            return Task.FromResult(Last!);
        }
        public Task<JournalEntry> CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef = null, string? pendingStepId = null, string? message = null, CancellationToken ct = default)
        {
            Calls.Add("close:" + phase);
            Last = entry with
            {
                Phase = phase,
                StashRef = stashRef ?? entry.StashRef,
                PendingStepId = pendingStepId,
                Message = message
            };
            return Task.FromResult(Last!);
        }
        public Task<JournalEntry> SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct = default)
        {
            Calls.Add("stash");
            Last = entry with
            {
                StashRef = stashRef
            };
            return Task.FromResult(Last!);
        }
        public Task<JournalEntry?> ActiveAsync(string repository, CancellationToken ct = default) => Task.FromResult(Last);
        public Task<JournalEntry?> UndoableAsync(string repository, CancellationToken ct = default) => Task.FromResult(Last);
        public Task<JournalEntry?> ByIdAsync(string repository, string id, CancellationToken ct = default) => Task.FromResult(Last);
        public IReadOnlyList<JournalEntry> Recent(string repository, int count = 20) => Last is null ? [] : [Last];

        Task<Result<JournalEntry>> IJournal.OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct) => Boundary.CaptureAsync(() => OpenAsync(repository, kind, planned, ct), ProblemCodes.StoreWrite);

        Task<Result<JournalEntry>> IJournal.StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct) => Boundary.CaptureAsync(() => StepAsync(entry, step, ct), ProblemCodes.StoreWrite);

        Task<Result<JournalEntry>> IJournal.CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef, string? pendingStepId, string? message, CancellationToken ct) => Boundary.CaptureAsync(() => CloseAsync(entry, phase, stashRef, pendingStepId, message, ct), ProblemCodes.StoreWrite);

        Task<Result<JournalEntry>> IJournal.SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct) => Boundary.CaptureAsync(() => SetStashAsync(entry, stashRef, ct), ProblemCodes.StoreWrite);

        Task<Result<JournalEntry?>> IJournal.ActiveAsync(string repository, CancellationToken ct) => Boundary.CaptureAsync(() => ActiveAsync(repository, ct), ProblemCodes.StoreWrite);

        Task<Result<JournalEntry?>> IJournal.UndoableAsync(string repository, CancellationToken ct) => Boundary.CaptureAsync(() => UndoableAsync(repository, ct), ProblemCodes.StoreWrite);

        Task<Result<JournalEntry?>> IJournal.ByIdAsync(string repository, string id, CancellationToken ct) => Boundary.CaptureAsync(() => ByIdAsync(repository, id, ct), ProblemCodes.StoreWrite);

        Result<IReadOnlyList<JournalEntry>> IJournal.Recent(string repository, int count) => Boundary.Capture(() => Recent(repository, count), ProblemCodes.StoreWrite);
    }
}
