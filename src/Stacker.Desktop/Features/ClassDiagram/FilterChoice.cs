using Stacker.Core;

namespace Stacker.Desktop;

internal sealed record FilterChoice(CSharpRelationshipKind? Kind, string Text)
{
    public override string ToString() => Text;
}
