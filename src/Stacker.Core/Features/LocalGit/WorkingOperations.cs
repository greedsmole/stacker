namespace Stacker.Core;

/// <summary>Names an unfinished operation in the form a sentence needs ("A rebase is in progress").</summary>
/// <remarks>One place supplies the article and noun so blockers, banners and labels cannot drift apart as the wording changes.</remarks>
public static class WorkingOperations
{
    /// <summary>Returns the article-plus-noun phrase for an operation, ready to be followed by "is in progress".</summary>
    /// <remarks><see cref="WorkingOperation.Am"/> is named a patch application, its user-facing form; <see cref="WorkingOperation.None"/> falls back to the neutral "An operation" rather than an empty string so a sentence never starts mid-thought.</remarks>
    /// <param name="operation">Operation to name.</param>
    /// <returns>The phrase with its article.</returns>
    public static string Noun(WorkingOperation operation) => operation switch
    {
        WorkingOperation.Rebase => "A rebase",
        WorkingOperation.Am => "A patch application",
        WorkingOperation.Merge => "A merge",
        WorkingOperation.CherryPick => "A cherry-pick",
        WorkingOperation.Revert => "A revert",
        _ => "An operation"
    };
}
