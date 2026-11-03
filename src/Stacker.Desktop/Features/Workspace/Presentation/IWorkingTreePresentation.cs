// Generated presentation boundary; application state lives in WorkspaceRuntime/WorkspaceSession.
namespace Stacker.Desktop.ViewModels;
internal interface IWorkingTreePresentation
{
    global::System.Threading.Tasks.Task<global::Stacker.Core.RestackPlan?> BuildRestackPlanAsync(global::System.Boolean @preserveMerges = false);
    global::System.Boolean CanRestoreRecovery { get; }
    global::System.Boolean CanRestoreStash { get; }
    global::System.String Error { get; set; }
    global::System.Boolean HasRecovery { get; }
    global::System.Boolean IsSyncing { get; set; }
    void Notify(global::System.String @name);
    global::System.String RecoveryAction { get; }
    global::System.String RecoverySummary { get; }
    global::System.Threading.Tasks.Task ReloadAsync();
    global::System.Threading.Tasks.Task<global::Stacker.Core.RestackOutcome?> RestackAsync(global::Stacker.Core.RestackPlan @plan,global::System.IProgress<global::System.String>? @progress = null);
    global::System.Collections.Generic.IReadOnlyList<global::Stacker.Core.Framework.SavedStash> SavedStashes { get; set; }
    global::Stacker.Core.AppSettings Settings { get; set; }
    global::System.String Status { get; set; }
    global::System.String SyncProgress { get; set; }
    global::Stacker.Core.WorkingTreeState? WorkingTree { get; set; }
}
