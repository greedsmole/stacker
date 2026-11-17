using Stacker.Core.Framework;

namespace Stacker.Core;

/// <summary>A source coordinate on one exact side of a diff. Lines are one-based and columns are UTF-16 offsets.</summary>
public sealed record SymbolNavigationRequest(string Root, string Commit, string Path, int Line, int Column,
    SymbolNavigationKind Kind);
