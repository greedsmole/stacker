using Stacker.Core.Framework;

namespace Stacker.Core;

public sealed record SymbolNavigationResult(string Symbol, IReadOnlyList<SymbolLocation> Locations, bool Truncated);
