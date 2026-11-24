using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

/// <summary>Uses the extension as the owner of its stack metadata and local rebase recovery.</summary>
public sealed partial class GhStackClient(IProcessRunner processes, GitExecutable gitExecutable, GhExecutable gh,
    CommandBus commands, IJournal journal, GitCommandLog? log = null) : IGhStackClient
{
    private const string Prefix = "gh-stack.";
    private const string RebaseKind = Prefix + "rebase";
    private readonly GitRunner _git = new(processes, gitExecutable);

    public Task<Result<GhStackTool>> ProbeAsync(GhStackTarget target, CancellationToken ct = default) =>
        Boundary.CaptureAsync(() => Probe(target, ct), ProblemCodes.GitFailed);

    private async Task<GhStackTool> Probe(GhStackTarget target, CancellationToken ct)
    {
        var result = await Run(target, ["--version"], GitRunner.RecoveryTimeout, ct);
        const string prefix = "gh stack version ";
        var version = result.StdOut.Trim();
        if (result.ExitCode != 0 || !version.StartsWith(prefix, StringComparison.Ordinal)
            || !Version.TryParse(version[prefix.Length..].TrimStart('v'), out var parsed))
            throw new StackerException("Install the GitHub CLI stack extension with gh extension install github/gh-stack.");
        return new(parsed);
    }

    public Task<Result<GhStackView>> LoadAsync(GhStackTarget target, CancellationToken ct = default) =>
        Boundary.CaptureAsync(() => Load(target, ct), ProblemCodes.GitFailed);

    private async Task<GhStackView> Load(GhStackTarget target, CancellationToken ct)
    {
        var current = await _git.CurrentBranchRefAsync(target.Root, ct);
        if (current.Length == 0)
            return new(null, "Check out a stack branch to load its layers.");
        var result = await Run(target, ["view", "--json"], GitRunner.NetworkTimeout, ct);
        if (result.ExitCode is 2 or 6)
            return new(null, Detail(result, result.ExitCode == 6 ? "Check out a layer to choose its stack." : "No stack is available for this checkout."));
        if (result.ExitCode != 0)
            throw new StackerException(Detail(result, "GitHub CLI could not load the stack."));

        using var json = JsonDocument.Parse(result.StdOut);
        var data = json.RootElement;
        var trunk = ReadName(data, "trunk");
        var branch = ReadName(data, "currentBranch");
        var layers = data.GetProperty("branches").EnumerateArray().Select(item => new GhStackBranch(ReadName(item, "name"),
            Flag(item, "isMerged"), Flag(item, "isQueued"), Flag(item, "needsRebase"))).ToArray();
        if (layers.Length == 0 || layers.Select(layer => layer.Name).Append(trunk).Distinct(StringComparer.Ordinal).Count() != layers.Length + 1)
            throw new StackerException("GitHub CLI returned an invalid stack.");
        if (current != LocalBranch.Prefix + branch || current != await _git.CurrentBranchRefAsync(target.Root, ct))
            throw new StackerException("The checkout changed while loading the GitHub CLI stack. Load it again.");
        return new(new(trunk, branch, layers), "");
    }

    public Task<Result<GhStackRebasePlan>> PreviewRebaseAsync(GhStackTarget target, CancellationToken ct = default) =>
        Boundary.CaptureAsync(async () =>
        {
            await RequireWriteTool(target, ct);
            await RequireClean(target.Root, ct);
            var stack = await RequireStack(target, ct);
            return await BuildRebasePlan(target, stack, ct);
        }, ProblemCodes.GitFailed);

    public Task<CommandOutcome> CreateAsync(GhStackTarget target, string trunk, string branch, CancellationToken ct = default) =>
        Execute(target.Root, Prefix + "init", "Created GitHub CLI stack", [JournalStep.Planned("cli", Prefix + "init", branch)],
            async token =>
            {
                await RequireWriteTool(target, token);
                await RequireClean(target.Root, token);
                await RequireRef(target.Root, trunk, token);
                await RequireNewBranch(target.Root, branch, token);
            }, (_, token) => Run(target, ["init", "--base", trunk, "--", branch], GitRunner.WriteTimeout, token), ct);

    public Task<CommandOutcome> AddAsync(GhStackTarget target, string branch, CancellationToken ct = default) =>
        Execute(target.Root, Prefix + "add", "Added GitHub CLI layer", [JournalStep.Planned("cli", Prefix + "add", branch)],
            async token =>
            {
                await RequireWriteTool(target, token);
                await RequireClean(target.Root, token);
                await RequireNewBranch(target.Root, branch, token);
            }, async (_, token) =>
            {
                var stack = await RequireStack(target, token);
                if (stack.Branches.LastOrDefault(layer => !layer.IsMerged)?.Name != stack.CurrentBranch)
                    throw new StackerException(Rejected("Check out the tip of the GitHub CLI stack before adding a layer."));
                return await Run(target, ["add", "--", branch], GitRunner.WriteTimeout, token);
            }, ct);

    public Task<CommandOutcome> RebaseAsync(GhStackRebasePlan plan, CancellationToken ct = default) =>
        Execute(plan.Target.Root, RebaseKind, "Rebased upper GitHub CLI layers",
            [JournalStep.Planned("cli", RebaseKind, plan.Stack.CurrentBranch),
                .. plan.UpperLayers.Select((reference, i) => JournalStep.Planned($"layer-{i}", "gh-stack.layer", reference.Name)
                    with { OldSha = reference.Sha })],
            async token =>
            {
                await RequireWriteTool(plan.Target, token);
                await RequireClean(plan.Target.Root, token);
                await VerifyRefs(plan, token);
            }, async (_, token) =>
            {
                var now = await BuildRebasePlan(plan.Target, await RequireStack(plan.Target, token), token);
                if (now.Stack.CurrentBranch != plan.Stack.CurrentBranch || !now.Refs.SequenceEqual(plan.Refs)
                    || !now.UpperLayers.SequenceEqual(plan.UpperLayers))
                    throw new StackerException(Rejected("The GitHub CLI stack changed. Open a new rebase preview."));
                return await Run(plan.Target, ["rebase", "--no-trunk"], GitRunner.WriteTimeout, token);
            }, ct);

    public Task<Result<GhStackRecovery?>> RecoveryAsync(string root, CancellationToken ct = default) =>
        Boundary.CaptureAsync(async () =>
        {
            if (Pending(root) is not { } entry)
                return null;
            var state = await _git.StatusAsync(root, ct);
            var active = state.Operation == WorkingOperation.Rebase && await MatchesRebase(root, entry, ct);
            return new GhStackRecovery(entry.Id, entry.Message ?? "Inspect the interrupted GitHub CLI operation.",
                active && !state.HasConflicts, active, !state.NeedsAttention);
        }, ProblemCodes.GitFailed);

    public Task<CommandOutcome> RecoverAsync(GhStackTarget target, string operationId, bool abort, CancellationToken ct = default)
    {
        var kind = Prefix + (abort ? "abort" : "continue");
        return Execute(target.Root, kind, abort ? "Aborted GitHub CLI rebase" : "Finished GitHub CLI rebase",
            [JournalStep.Planned("cli", kind, operationId)], async token =>
            {
                await RequireWriteTool(target, token);
                var operation = (await RecoveryAsync(target.Root, token)).OrThrow();
                if (operation?.OperationId != operationId || !(abort ? operation.CanAbort : operation.CanContinue))
                    throw new StackerException("Verify the stopped rebase in this worktree and stage resolved files before continuing.");
            }, async (_, token) =>
            {
                var original = Pending(target.Root) ?? throw new StackerException("The recorded rebase is no longer active.");
                var result = await Run(target, ["rebase", abort ? "--abort" : "--continue"], GitRunner.WriteTimeout, token);
                if (result.ExitCode == 0)
                {
                    using var completion = new CancellationTokenSource(GitRunner.RecoveryTimeout);
                    (await journal.CloseAsync(original, abort ? JournalPhase.RolledBack : JournalPhase.Applied,
                        message: abort ? "GitHub CLI restored the stack." : "GitHub CLI finished the rebase.", ct: completion.Token)).OrThrow();
                }
                return result;
            }, ct, recovering: true);
    }

    public Task<CommandOutcome> AcknowledgeAsync(string root, string operationId, CancellationToken ct = default) =>
        commands.RunAsync(RepoRoot.Parse(root).OrThrow(),
            new GhStackCommand(Prefix + "acknowledge", "Reviewed the GitHub CLI operation", [], async token =>
            {
                var recovery = (await RecoveryAsync(root, token)).OrThrow();
                if (recovery?.OperationId != operationId || !recovery.CanAcknowledge)
                    throw new StackerException("Finish the Git operation before acknowledging it.");
            }, async (_, token) =>
            {
                var entry = Pending(root) ?? throw new StackerException("The operation is no longer active.");
                (await journal.CloseAsync(entry, JournalPhase.Dismissed, message: "Reviewed current repository state.", ct: token)).OrThrow();
                return CommandOutcome.Done("Reviewed the GitHub CLI operation");
            }), ct: ct);

    private Task<CommandOutcome> Execute(string root, string kind, string description, IReadOnlyList<JournalStep> steps,
        Func<CancellationToken, Task> validate, Func<CommandContext, CancellationToken, Task<ProcessResult>> run,
        CancellationToken ct, bool recovering = false) =>
        commands.RunAsync(RepoRoot.Parse(root).OrThrow(), new GhStackCommand(kind, description, steps, validate, async (context, token) =>
        {
            (await journal.StepAsync(context.Entry, steps[0] with { State = StepState.Started }, token)).OrThrow();
            ProcessResult result;
            try { result = await run(context, token); }
            catch (StackerException ex) when (ex.Problem?.Certainty == EffectCertainty.Rejected)
            {
                using var refused = new CancellationTokenSource(GitRunner.RecoveryTimeout);
                (await journal.StepAsync(context.Entry, steps[0] with { State = StepState.Failed }, refused.Token)).OrThrow();
                return CommandOutcome.Refused(ex.Message);
            }

            using var completion = new CancellationTokenSource(GitRunner.RecoveryTimeout);
            var success = result.ExitCode == 0;
            (await journal.StepAsync(context.Entry, steps[0] with { State = success ? StepState.Done : StepState.Unknown },
                completion.Token)).OrThrow();
            if (success)
                return CommandOutcome.Done(description);
            var detail = Detail(result, "Check the GitHub CLI operation in Operations and recovery.");
            return recovering ? CommandOutcome.Refused(detail) : CommandOutcome.Attention(detail);
        }), ct: ct);

    private JournalEntry? Pending(string root) => journal.Recent(RepoRoot.Parse(root).OrThrow().Value, int.MaxValue).OrThrow()
        .FirstOrDefault(entry => entry.NeedsAttention && entry.Kind is Prefix + "init" or Prefix + "add" or RebaseKind);

    private async Task<bool> MatchesRebase(string root, JournalEntry entry, CancellationToken ct)
    {
        if (entry.Kind != RebaseKind)
            return false;
        foreach (var layer in entry.Steps.Where(step => step.Kind == "gh-stack.layer"))
            if (await _git.MatchesRebaseAsync(root, layer, null, ct))
                return true;
        return false;
    }

    private async Task<GhStackSnapshot> RequireStack(GhStackTarget target, CancellationToken ct)
    {
        var view = await Load(target, ct);
        return view.Stack ?? throw new StackerException(Rejected(view.Message));
    }

    private async Task RequireWriteTool(GhStackTarget target, CancellationToken ct)
    {
        if (!(await Probe(target, ct)).CanManage)
            throw new StackerException("Install GitHub CLI stack extension 0.3.x to manage its stacks in Stacker.");
    }

    private async Task RequireClean(string root, CancellationToken ct)
    {
        var state = await _git.StatusAsync(root, ct);
        if (state.NeedsAttention || Pending(root) is not null)
            throw new StackerException("Finish the current Git or GitHub CLI operation first.");
        if (state.HasTrackedChanges)
            throw new StackerException("Commit or stash tracked changes before changing a stack.");
    }

    private async Task<GhStackRebasePlan> BuildRebasePlan(GhStackTarget target, GhStackSnapshot stack, CancellationToken ct)
    {
        var refs = new List<BranchRef> { await RequireRef(target.Root, stack.Trunk, ct) };
        var upper = new List<BranchRef>();
        var occupied = await _git.OtherWorktreeBranchesAsync(target.Root, ct);
        for (var i = 0; i < stack.Branches.Count; i++)
        {
            var layer = stack.Branches[i];
            if (layer.IsMerged)
                continue;
            var reference = await RequireRef(target.Root, layer.Name, ct);
            refs.Add(reference);
            if (i == 0)
                continue;
            if (occupied.Contains(reference.Name, StringComparer.Ordinal))
                throw new StackerException(Rejected($"{layer.Name} is checked out in another worktree."));
            upper.Add(reference);
        }
        if (upper.Count == 0)
            throw new StackerException(Rejected("This stack has no upper layers to rebase."));
        return new(target, stack, refs, upper);
    }

    private async Task VerifyRefs(GhStackRebasePlan plan, CancellationToken ct)
    {
        if (await _git.CurrentBranchRefAsync(plan.Target.Root, ct) != LocalBranch.Prefix + plan.Stack.CurrentBranch)
            throw new StackerException("The checkout changed. Open a new rebase preview.");
        foreach (var reference in plan.Refs)
            if (await _git.ShaAsync(plan.Target.Root, reference.Name, ct) != reference.Sha)
                throw new StackerException($"{StackRefs.Short(reference.Name)} moved. Open a new rebase preview.");
    }

    private async Task<BranchRef> RequireRef(string root, string name, CancellationToken ct)
    {
        var reference = LocalBranch.Prefix + ValidateName(name);
        var sha = await _git.ShaAsync(root, reference, ct) ?? throw new StackerException(Rejected($"Local branch {name} is missing."));
        return new(reference, sha);
    }

    private async Task RequireNewBranch(string root, string name, CancellationToken ct)
    {
        ValidateName(name);
        var valid = await _git.Run(root, ["check-ref-format", "--branch", name], ct, allowFailure: true);
        if (valid.ExitCode != 0 || valid.StdOut.TrimEnd('\r', '\n') != name)
            throw new StackerException("Enter a valid new branch name.");
        if (await _git.ShaAsync(root, LocalBranch.Prefix + name, ct) is not null)
            throw new StackerException($"{name} already exists. Choose a new branch name.");
    }

    private static string ReadName(JsonElement value, string key) => ValidateName(value.GetProperty(key).GetString() ?? "");
    private static bool Flag(JsonElement value, string key) => value.TryGetProperty(key, out var flag) && flag.GetBoolean();
    private static string ValidateName(string value) => value.Length > 0 && value[0] != '-' &&
        !value.StartsWith("refs/", StringComparison.Ordinal) && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl)
        ? value : throw new StackerException(Rejected("Enter a short local branch name."));
    private static Problem Rejected(string message) => Problem.Of(ProblemCodes.StateInvalid, message) with { Certainty = EffectCertainty.Rejected };
    private static string Detail(ProcessResult result, string fallback) =>
        string.IsNullOrWhiteSpace(result.StdErr) ? fallback : GitCommandLog.Redact(result.StdErr.Trim());
}
