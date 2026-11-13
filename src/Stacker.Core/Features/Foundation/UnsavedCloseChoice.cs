using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>The user's answer when closing could not persist the workspace or the drafts.</summary>
/// <remarks>The close handler asks again after a retry, so these choices drive a loop rather than a one-shot decision.
/// Only <see cref="Discard"/> loses the unsaved text; every other choice either keeps the window open or writes a copy.
/// The recorded intent is to keep the close decision with the user instead of dropping work silently.</remarks>
public enum UnsavedCloseChoice
{
    /// <summary>Abandon the close and go back to editing; foreground work resumes.</summary>
    ContinueEditing,
    /// <summary>Attempt the persistence step again after a failure, staying inside the close loop.</summary>
    Retry,
    /// <summary>Write the unsent review text to a file the user chooses, then continue closing.</summary>
    SaveCopy,
    /// <summary>Close without saving. The unsaved text is lost.</summary>
    Discard
}
