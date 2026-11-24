namespace Stacker.Core;

public sealed record GhStackTool(Version Version)
{
    public bool CanManage => Version.Major == 0 && Version.Minor == 3;
}
