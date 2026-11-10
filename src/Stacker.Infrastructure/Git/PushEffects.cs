using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Publishes restacked stack branches to origin with one force-with-lease push per branch, each recorded as a
/// journal step.</summary>
/// <remarks>The whole remote snapshot is verified before the first branch is published: every branch is fetched and
/// compared with the commit the approved plan captured, so a remote that moved after the preview stops the run before
/// anything is overwritten, and nothing is pushed at all. The publication itself is a force-with-lease whose expected
/// value is that captured commit, which makes the check atomic inside Git instead of a read followed by a blind force.
/// A branch that did not exist at preview time is leased against the empty string, which requires it to still be
/// absent. Each branch is one <c>push</c> step from <see cref="StepState.Started"/> to <see cref="StepState.Done"/>, so
/// a partial publication is recorded branch by branch.
/// <para>Remote effects are never promised as automatically rolled back. Git has no transaction across branches and no
/// way to retract a push that already landed, so compensating would mean pushing the old commits back under a fresh
/// lease, which is itself an unverified remote write that could discard work someone fetched or merged in the
/// meantime. The journal therefore keeps push steps out of <see cref="JournalEntry.Restorable"/>, which admits only
/// recorded rebases, and the messages tell the user what was published and that origin must be inspected before
/// retrying.</para>
/// <para>What "unknown" means for a push: a step left in <see cref="StepState.Started"/> records that Git was asked to
/// publish a branch, not that it did. The network may have carried the push and lost the answer, and no local read can
/// distinguish that from a push that never left; the step's old value is the remote commit the lease expected, which
/// is exactly the evidence to compare against origin. Nothing is retried automatically, because a retry would either
/// fail the lease or overwrite a branch that is no longer the one the plan approved.</para></remarks>
/// <param name="git">Runner that owns the process policy, the write gate and the ref reads.</param>
/// <param name="journal">Write-ahead journal; a branch's step is stored before its push is dispatched.</param>
internal sealed class PushEffects(GitRunner git, IJournal journal)
{
    /// <summary>Publishes the branches the restack rewrote, refusing before the first push when the remote no longer
    /// matches the approved plan.</summary>
    /// <remarks>Only rewritten layers are published, because an up-to-date layer already has the remote commit the
    /// user approved; a first publication also sets the branch's upstream so later commands can use the short name.
    /// The returned names describe what actually landed, so a caller can report them as effects.</remarks>
    /// <param name="plan">Approved plan; its <see cref="RestackPlan.PushSteps"/> are the branches to publish and their
    /// captured remote commits are the lease values.</param>
    /// <param name="ct">Cancels the loop between branches; a push already dispatched is not recalled.</param>
    /// <param name="context">Running journal entry when the command runner opened one, so each branch is recorded; null
    /// for a direct call that wants the push without journaling.</param>
    /// <returns>The short names of the branches that were published, in push order.</returns>
    /// <exception cref="StackerException">The repository has no origin, there is nothing to push, a branch moved on
    /// origin after the preview, or a push failed.</exception>
    public Task<IReadOnlyList<string>> PushAsync(RestackPlan plan, CancellationToken ct = default, CommandContext? context = null) =>
        PushStepsAsync(plan, plan.PushSteps, context, ct);
    /// <summary>Publishes every publishable local layer, including layers the restack did not rewrite.</summary>
    /// <remarks>Whole-stack mode also moves a remote branch that is simply behind the local commit, so the remote stack
    /// mirrors the local one; blocked layers stay excluded because the plan could not establish them as safe to
    /// publish. The remote snapshot is still verified for every branch before the first push.</remarks>
    /// <param name="plan">Approved plan; its <see cref="RestackPlan.StackPushSteps"/> are the branches to publish.</param>
    /// <param name="ct">Cancels the loop between branches; a push already dispatched is not recalled.</param>
    /// <param name="context">Running journal entry when the command runner opened one, so each branch is recorded; null
    /// for a direct call that wants the push without journaling.</param>
    /// <returns>The short names of the branches that were published, in push order.</returns>
    /// <exception cref="StackerException">The repository has no origin, there is nothing to push, a branch moved on
    /// origin after the preview, or a push failed.</exception>
    public Task<IReadOnlyList<string>> PushStackAsync(RestackPlan plan, CancellationToken ct = default, CommandContext? context = null) =>
        PushStepsAsync(plan, plan.StackPushSteps, context, ct);

    /// <summary>Verifies the remote snapshot for every requested branch, then force-pushes each one from a pinned local
    /// commit while recording its journal step.</summary>
    /// <remarks>The write gate is held for the whole sequence, so an interleaved command cannot move a branch between
    /// the verification and its push. Verification is a full pass over the requested branches before any publication:
    /// each branch is fetched with an explicit refspec, a missing remote branch is accepted only through Git's
    /// "couldn't find remote ref" answer, and every other fetch failure, as well as any difference from the captured
    /// commit, refuses the run with nothing published. Local commits are resolved in the same pass, before the first
    /// push, so a ref that moves later cannot change what is being sent.
    /// <para>A push failure stops the loop while earlier branches remain published; the step for the failed branch
    /// stays <see cref="StepState.Started"/> because its outcome is unknown, and the exception names what already
    /// landed. There is no compensating push, for the reasons on the type.</para>
    /// <para>A successful first publication sets the branch's upstream, and that follow-up is allowed to fail without
    /// failing the push, because the publication itself has already succeeded.</para></remarks>
    /// <param name="plan">Plan supplying the repository root and the origin check.</param>
    /// <param name="steps">Branches to publish, with the remote commit each lease expects.</param>
    /// <param name="context">Running journal entry, or null to publish without recording steps.</param>
    /// <param name="ct">Cancels between branches.</param>
    /// <returns>The short names published so far, in order.</returns>
    /// <exception cref="StackerException">A precondition failed, a branch drifted from the preview, or a push
    /// failed.</exception>
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
