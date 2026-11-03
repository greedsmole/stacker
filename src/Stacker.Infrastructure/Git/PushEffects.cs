using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

internal sealed class PushEffects(GitRunner git, IJournal journal)
{
    public Task<IReadOnlyList<string>> PushAsync(RestackPlan plan, CancellationToken ct = default, CommandContext? context = null) =>
        PushStepsAsync(plan, plan.PushSteps, context, ct);
    public Task<IReadOnlyList<string>> PushStackAsync(RestackPlan plan, CancellationToken ct = default, CommandContext? context = null) =>
        PushStepsAsync(plan, plan.StackPushSteps, context, ct);

    private async Task<IReadOnlyList<string>> PushStepsAsync(RestackPlan plan, IReadOnlyList<PushStep> steps, CommandContext? context, CancellationToken ct)
    {
        await git.Gate.WaitAsync(ct);
        var pushed = new List<string>();
        try
        {
            if (!plan.HasOrigin) throw new StackerException("This repository has no origin remote. Restacking stays local.");
            if (steps.Count == 0) throw new StackerException("There is nothing to push for this stack.");
            var root = plan.RepositoryRoot;
            var prepared = new List<(PushStep Step, string Local)>();
            // Verify the entire remote snapshot before publishing the first branch.
            foreach (var step in steps)
            {
                var name = StackRefs.Short(step.Branch);
                var fetch = await git.Run(root, ["fetch", "--no-tags", "--no-write-fetch-head", "origin",
                    $"+{step.Branch}:refs/remotes/origin/{name}"], ct, allowFailure: true, timeout: GitRunner.NetworkTimeout);
                if (fetch.ExitCode != 0 && !fetch.StdErr.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
                    throw new StackerException($"Cannot read {name} from origin. {fetch.StdErr.Trim()}");
                var remote = fetch.ExitCode == 0 ? await git.ShaAsync(root, "refs/remotes/origin/" + name, ct) : null;
                if (remote != step.RemoteTip)
                    throw new StackerException($"{name} moved on origin after the preview. Nothing was pushed; refresh before retrying.");
                prepared.Add((step, await git.ShaAsync(root, step.Branch, ct) ?? throw new StackerException($"{name} no longer exists.")));
            }
            foreach (var (step, local) in prepared)
            {
                var name = StackRefs.Short(step.Branch);
                var started = new JournalStep(step.Branch, "push", step.Branch, step.RemoteTip, local, StepState.Started);
                if (context is not null) (await journal.StepAsync(context.Entry, started, ct)).OrThrow();
                // An empty lease requires the branch to still be absent; the source is an immutable SHA.
                var push = await git.Run(root, ["push", $"--force-with-lease={step.Branch}:{step.RemoteTip ?? ""}",
                    "origin", $"{local}:{step.Branch}"], ct, allowFailure: true, timeout: GitRunner.NetworkTimeout);
                if (push.ExitCode != 0)
                    throw new StackerException($"Pushing {name} failed; inspect origin before retrying. {push.StdErr.Trim()}");
                pushed.Add(name);
                if (context is not null) (await journal.StepAsync(context.Entry, started with { State = StepState.Done }, ct)).OrThrow();
                if (step.RemoteTip is null)
                    await git.Run(root, ["branch", "--set-upstream-to=origin/" + name, name], ct, allowFailure: true);
            }
            return pushed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && pushed.Count > 0)
        { throw new StackerException($"Published {string.Join(", ", pushed)}, then stopped. {ex.Message}"); }
        finally { git.Gate.Release(); }
    }
}
