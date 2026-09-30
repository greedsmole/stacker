// Purpose: Process boundary scenarios for argument fidelity, stdin/stdout/stderr, timeouts, cancellation, and output limits.
using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class ProcessRunnerTests
{
    private static ProcessRequest Probe(string mode, params string[] args) => new("dotnet", [typeof(ProcessProbe).Assembly.Location, mode, .. args]);
    // Scenario: Arguments are passed literally and stdin is closed.
    [Fact]
    public async Task Arguments_are_passed_literally_and_stdin_is_closed()
    {
        var runner = new ProcessRunner(); var values = new[] { "two words", "$(echo bad)", "&stuff", "üникод", "\"quoted\"" };
        var result = await runner.RunAsync(Probe("args", values)); Assert.Equal(0, result.ExitCode);
        Assert.Equal(values, result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("", (await runner.RunAsync(Probe("stdin"))).StdOut);
        Assert.Equal("{\"body\":\"Unicode ü\"}", (await runner.RunAsync(Probe("stdin") with { StandardInput = "{\"body\":\"Unicode ü\"}" })).StdOut);
    }
    // Scenario: Reads both pipes without deadlock and preserves exit code.
    [Fact]
    public async Task Reads_both_pipes_without_deadlock_and_preserves_exit_code()
    {
        var runner = new ProcessRunner(); var flood = await runner.RunAsync(Probe("flood")); Assert.Equal(2_048_000, flood.StdOut.Length); Assert.Equal(2_048_000, flood.StdErr.Length);
        var failed = await runner.RunAsync(Probe("exit")); Assert.Equal(7, failed.ExitCode); Assert.Equal("intentional error", failed.StdErr);
    }
    // Scenario: Output limit timeout and cancellation end the process.
    [Fact]
    public async Task Output_limit_timeout_and_cancellation_end_the_process()
    {
        var runner = new ProcessRunner();
        await Assert.ThrowsAsync<OutputLimitException>(() => runner.RunAsync(Probe("flood") with { MaxOutputBytes = 1000 }));
        await Assert.ThrowsAsync<StackerException>(() => runner.RunAsync(Probe("delay") with { Timeout = TimeSpan.FromMilliseconds(200) }));
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Probe("delay"), cts.Token));
    }
    // Scenario: Environment removal is limited to child process.
    [Fact]
    public async Task Environment_removal_is_limited_to_child_process()
    {
        var request = Probe("env", "STACKER_TEST_TOKEN") with { Environment = new Dictionary<string,string> { ["STACKER_TEST_TOKEN"] = "test-value" } };
        var runner = new ProcessRunner();
        Assert.Equal("test-value", (await runner.RunAsync(request)).StdOut);
        Assert.Equal("absent", (await runner.RunAsync(request with { UnsetEnvironment = ["STACKER_TEST_TOKEN"] })).StdOut);
        Assert.Equal("test-value", (await runner.RunAsync(request)).StdOut);
    }
    // Scenario: Missing executable has actionable error.
    [Fact]
    public async Task Missing_executable_has_actionable_error()
    {
        var exception = await Assert.ThrowsAsync<StackerException>(() => new ProcessRunner().RunAsync(new("stacker-missing-" + Guid.NewGuid(), [])));
        Assert.Contains("installation and path", exception.Message);
    }
}
