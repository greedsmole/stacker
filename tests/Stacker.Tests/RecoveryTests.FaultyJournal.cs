using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;

public sealed partial class RecoveryTests
{
    /// <summary>A journal that fails one step, standing in for any error between an effect and its record.</summary>
    private sealed class FaultyJournal(IJournal inner, int failOnStep = 0, JournalPhase? failAfterClose = null,
        JournalPhase? failBeforeClose = null) : IJournal
    {
        private int _steps;
        private bool _failedClose;
        public Task<JournalEntry> OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.OpenAsync(repository, kind, planned, ct));
        public Task<JournalEntry> StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct = default)
        {
            _steps++;
            if (_steps == failOnStep)
                throw new InvalidOperationException("injected journal failure");
            return Boundary.UnwrapAsync(inner.StepAsync(entry, step, ct));
        }
        public async Task<JournalEntry> CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef = null, string? pendingStepId = null, string? message = null, CancellationToken ct = default)
        {
            if (!_failedClose && entry.Kind == "restack" && phase == failBeforeClose)
            {
                _failedClose = true;
                throw new InvalidOperationException("injected close write failure");
            }
            var saved = (await inner.CloseAsync(entry, phase, stashRef, pendingStepId, message, ct)).OrThrow();
            if (entry.Kind == "restack" && phase == failAfterClose)
                throw new InvalidOperationException("injected close acknowledgement loss");
            return saved;
        }
        public Task<JournalEntry> SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.SetStashAsync(entry, stashRef, ct));
        public Task<JournalEntry?> ActiveAsync(string repository, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.ActiveAsync(repository, ct));
        public Task<JournalEntry?> UndoableAsync(string repository, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.UndoableAsync(repository, ct));
        public Task<JournalEntry?> ByIdAsync(string repository, string id, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.ByIdAsync(repository, id, ct));
        public IReadOnlyList<JournalEntry> Recent(string repository, int count = 20) => (inner.Recent(repository, count)).OrThrow();

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
