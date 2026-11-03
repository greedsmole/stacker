namespace Stacker.Core;

public static class RemoteRefs
{
    public static string HeadRef(PullRequest pr) => $"refs/heads/__pr_{pr.Number}";
    public static string BaseRef(PullRequest pr) => $"refs/heads/__base_{pr.Number}";
}
