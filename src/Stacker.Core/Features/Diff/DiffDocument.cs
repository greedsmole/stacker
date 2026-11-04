namespace Stacker.Core;

public enum DiffDocumentMode { Current,SinceReviewed,BetweenVersions,FileEvolution }
public sealed record DiffDocumentSection(string Title,string Root,string BaseSha,string HeadSha,IReadOnlyList<FileChange> Files);
public sealed record DiffDocument(DiffDocumentMode Mode,IReadOnlyList<DiffDocumentSection> Sections,string Explanation);
public sealed record NavigationTarget(string ChangeId,string? VersionId,string? Path,DiffDocumentMode Mode);
