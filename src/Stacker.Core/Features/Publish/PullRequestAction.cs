namespace Stacker.Core;

/// <summary>What publishing one layer must do to its pull request.</summary>
public enum PullRequestAction
{
    /// <summary>No open pull request exists for the layer's head branch, so one must be created into the parent.</summary>
    Create,
    /// <summary>An open pull request exists but its base is not the layer's parent, so it must be retargeted.</summary>
    UpdateBase,
    /// <summary>The pull request already has the right base, or the layer cannot be published from here.</summary>
    Unchanged
}
