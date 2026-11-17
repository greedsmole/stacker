using Stacker.Core;

namespace Stacker.Desktop;

internal sealed record SourceItem(string Label, string Commit, IReadOnlyList<SymbolLocation> Locations);
