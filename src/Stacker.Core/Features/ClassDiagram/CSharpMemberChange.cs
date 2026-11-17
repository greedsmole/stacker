namespace Stacker.Core;

public sealed record CSharpMemberChange(CSharpMember? Before, CSharpMember? After, CSharpChange Change)
{
    public CSharpMember Member => (After ?? Before)!;
}
