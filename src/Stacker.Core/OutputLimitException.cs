namespace Stacker.Core;

/// <summary>Reports that a process or patch produced more output than the display can accept.</summary>
/// <remarks>Thrown by the process runner when combined output would pass <see cref="ProcessRequest.MaxOutputBytes"/>, and by the patch reader when a
/// single patch is over its line budget. The message is already user-facing and suggests a smaller selection. A reader may catch this type to
/// drop one optional result (blob text used for highlighting) and keep the comparison usable, but a file list or patch that hits the limit is
/// reported instead of truncated.</remarks>
public sealed class OutputLimitException() : StackerException("Output exceeds the display limit. Select a smaller file or comparison.");
