namespace Stacker.Core;

public sealed record CSharpGraphRequest(RepositoryIdentity Repository, string Commit, string Entry);
