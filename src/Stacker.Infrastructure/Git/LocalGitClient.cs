using Stacker.Core.Features.LocalGit;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Compatibility facade over independently owned Git adapters.</summary>
public sealed class LocalGitClient : IGitBranchWriter
{
    private readonly CommandBus _bus;
    private readonly IJournal _journal;
    private readonly GitPlanReader _gitPlanReader;
    private readonly RestackEffects _restackEffects;
    private readonly RecoveryEffects _recoveryEffects;
    private readonly PushEffects _pushEffects;
    private readonly WorkingTreeEffects _workingTreeEffects;
    public LocalGitClient(IProcessRunner runner, GitExecutable executable, IGitRepositoryReader reader, IJournal journal, IClock clock, CommandBus? bus = null)
    {
        _journal = journal;
        _bus = bus ?? new CommandBus(new CommandRunner(journal));
        var git = new GitRunner(runner, executable);
        _gitPlanReader = new(git, reader); _recoveryEffects = new(git, journal);
        _restackEffects = new(git, journal, clock, _recoveryEffects);
        _pushEffects = new(git, journal); _workingTreeEffects = new(git, journal);
    }
    public Task<RestackPlan> PlanAsync(RepositorySnapshot repository, StackDefinition stack, bool preserveMerges = false, CancellationToken ct = default) => _gitPlanReader.PlanAsync(repository, stack, preserveMerges, ct);
    public Task<RestackOutcome> RestackAsync(RestackPlan plan, IProgress<string>? progress = null, CancellationToken ct = default) => Run(plan.RepositoryRoot, new RestackCommand(plan, (context, token) => Capture(() => _restackEffects.RestackAsync(plan, progress, token, context.Entry))), ct, progress);
    public Task<RestackOutcome> ContinueAsync(string root, string? editorCommand, CancellationToken ct = default) => Resolve(root, "continue", editorCommand, ct);
    public Task<RestackOutcome> SkipAsync(string root, CancellationToken ct = default) => Resolve(root, "skip", null, ct);
    public Task<RestackOutcome> AbortAsync(string root, CancellationToken ct = default) => Recover(root, ct);
    public Task<JournalEntry?> LastJournalAsync(string root, CancellationToken ct = default) => _recoveryEffects.LastJournalAsync(root, ct);
    public Task<JournalEntry?> ActiveJournalAsync(string root, CancellationToken ct = default) => _recoveryEffects.ActiveJournalAsync(root, ct);
    public Task DismissAsync(string root, CancellationToken ct = default) => Dismiss(root, ct);
    public Task<RestackOutcome> UndoLastAsync(string root, CancellationToken ct = default) => Run(root, new UndoCommand((_, token) => Capture(() => _recoveryEffects.UndoLastAsync(root, token))), ct);
    public Task<IReadOnlyList<string>> PushAsync(RestackPlan plan, CancellationToken ct = default) => Run(plan.RepositoryRoot, new PushCommand(plan, false, (context, token) => Capture(() => _pushEffects.PushAsync(plan, token, context))), ct);
    public Task<IReadOnlyList<string>> PushStackAsync(RestackPlan plan, CancellationToken ct = default) => Run(plan.RepositoryRoot, new PushCommand(plan, true, (context, token) => Capture(() => _pushEffects.PushStackAsync(plan, token, context))), ct);
    public Task<WorkingTreeState> StateAsync(string root, CancellationToken ct = default) => _workingTreeEffects.StateAsync(root, ct);
    public Task<string?> LastCommitSubjectAsync(string root, string branch, CancellationToken ct = default) => _workingTreeEffects.LastCommitSubjectAsync(root, branch, ct);
    public Task<string> CreateLayerAsync(string root, string branchName, string startRef, CancellationToken ct = default) => Run(root, new LayerCommand(NewBranch(branchName), (_, token) => Capture(() => _workingTreeEffects.CreateLayerAsync(root, branchName, startRef, token))), ct);
    public Task<IReadOnlyList<string>> StageAsync(string root, IReadOnlyList<string> paths, CancellationToken ct = default) => Run(root, new StageCommand(paths, (_, token) => Capture(() => _workingTreeEffects.StageAsync(root, paths, token))), ct);
    public Task<string> CommitAsync(string root, string message, bool stageAll, CancellationToken ct = default) => Run(root, new CommitCommand(CommitMessage.Parse(message).OrThrow(), (_, token) => Capture(() => _workingTreeEffects.CommitAsync(root, message, stageAll, token))), ct);
    public Task<string?> StashAsync(string root, string message, CancellationToken ct = default) => Run(root, new StashCommand(message, (_, token) => Capture(() => _workingTreeEffects.StashAsync(root, message, token))), ct);
    public Task<WorkingTreeState> RestoreStashAsync(string root, CancellationToken ct = default) => RestoreStash(root, ct);
    public Task SwitchAsync(string root, string branch, CancellationToken ct = default) => Switch(root, branch, ct);
    private static LocalBranch NewBranch(string name)
    {
        name = StackRefs.Short(name.Trim());
        if (name.Length == 0) throw new StackerException("Enter a branch name for the new layer.");
        var branch = LocalBranch.Parse("refs/heads/" + name);
        return branch.IsOk ? branch.Value : throw new StackerException($"'{name}' is not a valid branch name.");
    }
    private async Task<T> Run<T>(string root, EffectCommand<T> command, CancellationToken ct, IProgress<string>? progress = null)
    {
        var outcome = await _bus.RunAsync(RepoRoot.Parse(root).OrThrow(), command, progress, ct);
        if (outcome.Problem is not null && command.Value is { IsOk: true }) throw new StackerException(outcome.Message);
        if (command.Value is { } value) return value.OrThrow();
        if (outcome.Status == CommandStatus.Refused && typeof(T) == typeof(RestackOutcome))
            return (T)(object)new RestackOutcome(RestackStatus.Refused, null, [], outcome.Message, null);
        throw new StackerException(outcome.Message);
    }
    private async Task<RestackOutcome> Resolve(string root, string action, string? editor, CancellationToken ct)
    {
        var target = await _recoveryEffects.ActiveJournalAsync(root, ct);
        return await Run(root, new ResolveConflictCommand(action, (_, token) => Capture(() => action == "continue"
            ? _restackEffects.ContinueAsync(root, editor, token, target) : _restackEffects.SkipAsync(root, token, target))), ct);
    }
    public Task<Result<IReadOnlyList<SavedStash>>> SavedStashesAsync(string root, CancellationToken ct = default) => _journal.StashesAsync(root, ct);
    public Task<Result<WorkingTreeState>> RestoreRecordedStashAsync(string root, string sha, CancellationToken ct = default) =>
        Boundary.CaptureAsync(() => RestoreStash(root, ct, sha), ProblemCodes.GitFailed);
    private async Task<WorkingTreeState> RestoreStash(string root, CancellationToken ct, string? sha = null)
    {
        var stashes = (await _journal.StashesAsync(root, ct)).OrThrow();
        var selected = sha is null ? stashes.FirstOrDefault() : stashes.FirstOrDefault(stash => stash.Sha == sha);
        if (sha is not null && selected is null) throw new StackerException("This stash is no longer recorded. Refresh the saved stash list.");
        var target = selected is null ? await _recoveryEffects.ActiveJournalAsync(root, ct)
            : (await _journal.ByIdAsync(root, selected.EntryId, ct)).OrThrow()! with { StashRef = selected.Sha };
        return await Run(root, new RestoreStashCommand((_, token) => Capture(() => _workingTreeEffects.RestoreStashAsync(root, token, target))), ct);
    }
    private async Task<RestackOutcome> Recover(string root, CancellationToken ct)
    {
        var target = await _recoveryEffects.ActiveJournalAsync(root, ct);
        return await Run(root, new RecoverCommand((_, token) => Capture(() => _recoveryEffects.AbortAsync(root, token, target))), ct);
    }
    private async Task Dismiss(string root, CancellationToken ct)
    {
        var target = await _recoveryEffects.ActiveJournalAsync(root, ct);
        await Run(root, new DismissCommand((_, token) => CaptureUnit(() => _recoveryEffects.DismissAsync(root, token, target))), ct);
    }
    private async Task Switch(string root, string branch, CancellationToken ct)
    {
        await Run(root, new SwitchCommand(LocalBranch.Parse("refs/heads/" + StackRefs.Short(branch)).OrThrow(),
            (_, token) => CaptureUnit(() => _workingTreeEffects.SwitchAsync(root, branch, token))), ct);
    }
    private static async Task<Result<T>> Capture<T>(Func<Task<T>> effect)
    {
        try { return Result<T>.Ok(await effect()); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<T>.Fail(Boundary.Problem(ex, ProblemCodes.GitFailed)); }
    }
    private static Task<Result<Unit>> CaptureUnit(Func<Task> effect) => Capture(async () => { await effect(); return Unit.Value; });

    Task<Result<RestackPlan>> IGitBranchWriter.PlanAsync(RepositorySnapshot repository,StackDefinition stack,bool preserveMerges ,CancellationToken ct) => Boundary.CaptureAsync(() => PlanAsync(repository, stack, preserveMerges, ct), ProblemCodes.GitFailed);

    Task<Result<RestackOutcome>> IGitBranchWriter.RestackAsync(RestackPlan plan,IProgress<string>? progress ,CancellationToken ct) => Boundary.CaptureAsync(() => RestackAsync(plan, progress, ct), ProblemCodes.GitFailed);

    Task<Result<IReadOnlyList<string>>> IGitBranchWriter.PushAsync(RestackPlan plan,CancellationToken ct) => Boundary.CaptureAsync(() => PushAsync(plan, ct), ProblemCodes.GitFailed);

    Task<Result<JournalEntry?>> IGitBranchWriter.LastJournalAsync(string root,CancellationToken ct) => Boundary.CaptureAsync(() => LastJournalAsync(root, ct), ProblemCodes.GitFailed);

    Task<Result<RestackOutcome>> IGitBranchWriter.UndoLastAsync(string root,CancellationToken ct) => Boundary.CaptureAsync(() => UndoLastAsync(root, ct), ProblemCodes.GitFailed);

    Task<Result<string?>> IGitBranchWriter.LastCommitSubjectAsync(string root,string branch,CancellationToken ct) => Boundary.CaptureAsync(() => LastCommitSubjectAsync(root, branch, ct), ProblemCodes.GitFailed);

    Task<Result<string>> IGitBranchWriter.CreateLayerAsync(string root,string branchName,string startRef,CancellationToken ct) => Boundary.CaptureAsync(() => CreateLayerAsync(root, branchName, startRef, ct), ProblemCodes.GitFailed);

    Task<Result<IReadOnlyList<string>>> IGitBranchWriter.PushStackAsync(RestackPlan plan,CancellationToken ct) => Boundary.CaptureAsync(() => PushStackAsync(plan, ct), ProblemCodes.GitFailed);

    Task<Result<WorkingTreeState>> IGitBranchWriter.StateAsync(string root,CancellationToken ct) => Boundary.CaptureAsync(() => StateAsync(root, ct), ProblemCodes.GitFailed);

    Task<Result<IReadOnlyList<string>>> IGitBranchWriter.StageAsync(string root,IReadOnlyList<string> paths,CancellationToken ct) => Boundary.CaptureAsync(() => StageAsync(root, paths, ct), ProblemCodes.GitFailed);

    Task<Result<string>> IGitBranchWriter.CommitAsync(string root,string message,bool stageAll,CancellationToken ct) => Boundary.CaptureAsync(() => CommitAsync(root, message, stageAll, ct), ProblemCodes.GitFailed);

    Task<Result<string?>> IGitBranchWriter.StashAsync(string root,string message,CancellationToken ct) => Boundary.CaptureAsync(() => StashAsync(root, message, ct), ProblemCodes.GitFailed);

    Task<Result<WorkingTreeState>> IGitBranchWriter.RestoreStashAsync(string root,CancellationToken ct) => Boundary.CaptureAsync(() => RestoreStashAsync(root, ct), ProblemCodes.GitFailed);

    Task<Result<Unit>> IGitBranchWriter.SwitchAsync(string root,string branch,CancellationToken ct) => Boundary.CaptureAsync(() => SwitchAsync(root, branch, ct), ProblemCodes.GitFailed);

    Task<Result<RestackOutcome>> IGitBranchWriter.ContinueAsync(string root,string? editorCommand,CancellationToken ct) => Boundary.CaptureAsync(() => ContinueAsync(root, editorCommand, ct), ProblemCodes.GitFailed);

    Task<Result<RestackOutcome>> IGitBranchWriter.SkipAsync(string root,CancellationToken ct) => Boundary.CaptureAsync(() => SkipAsync(root, ct), ProblemCodes.GitFailed);

    Task<Result<RestackOutcome>> IGitBranchWriter.AbortAsync(string root,CancellationToken ct) => Boundary.CaptureAsync(() => AbortAsync(root, ct), ProblemCodes.GitFailed);

    Task<Result<JournalEntry?>> IGitBranchWriter.ActiveJournalAsync(string root,CancellationToken ct) => Boundary.CaptureAsync(() => ActiveJournalAsync(root, ct), ProblemCodes.GitFailed);

    Task<Result<Unit>> IGitBranchWriter.DismissAsync(string root,CancellationToken ct) => Boundary.CaptureAsync(() => DismissAsync(root, ct), ProblemCodes.GitFailed);
}
