namespace Stacker.Core;

public sealed record GhStackRecovery(string OperationId, string Message, bool CanContinue, bool CanAbort, bool CanAcknowledge);
