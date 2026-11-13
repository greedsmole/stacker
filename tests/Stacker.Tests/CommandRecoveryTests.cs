using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class CommandRecoveryTests
{

    [Fact]
    public async Task A_nonreversible_partial_failure_reports_completed_effects_without_claiming_rollback()
    {
        await using var f = new GitFixture();
        var journal = f.CreateJournal();
        var result = await new CommandRunner(journal).RunAsync(f.Root, new Partial());
        Assert.Equal(CommandStatus.PartiallyApplied, result.Status);
        Assert.Equal(new[] { "publish: A" }, result.Effects);
        Assert.True(result.NeedsUser);
        Assert.False(result.RolledBack);
        Assert.Equal(JournalPhase.NeedsUser, journal.Recent(f.Root).OrThrow()[0].Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Returned_failures_and_cancellation_both_compensate_with_a_live_cleanup_token(bool cancel)
    {
        await using var f = new GitFixture();
        var journal = f.CreateJournal();
        var command = new Recoverable(cancel);
        var result = await new CommandRunner(journal).RunAsync(f.Root, command);
        Assert.True(command.Restored);
        Assert.Equal(cancel ? CommandStatus.Cancelled : CommandStatus.RolledBack, result.Status);
        Assert.Equal(JournalPhase.RolledBack, journal.Recent(f.Root).OrThrow()[0].Phase);
    }
}
