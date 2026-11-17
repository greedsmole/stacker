namespace Stacker.Core;

public sealed record CSharpTypeChange(CSharpType? Before, CSharpType? After, CSharpChange Change, IReadOnlyList<CSharpMemberChange> Members)
{
    public CSharpType Type => (After ?? Before)!;
}
