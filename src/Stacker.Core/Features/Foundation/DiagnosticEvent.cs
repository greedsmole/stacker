using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One bounded diagnostic record describing a completed operation stage.</summary>
/// <param name="OperationId">Correlation id shared by the operation's bus run, journal entry and diagnostics, so one
/// user action can be followed across all three.</param>
/// <param name="Stage">Stage name, for example the command outcome or the Git read that produced it.</param>
/// <param name="ProblemCode">Catalogued problem code when the stage failed; <see langword="null"/> on success.</param>
/// <param name="DurationMs">Wall-clock duration of the stage in milliseconds.</param>
/// <param name="Bytes">Bytes read or produced, when the stage measures them; 0 otherwise.</param>
/// <remarks>The record is deliberately limited to identifiers, stage names, durations, byte counts and problem codes:
/// it carries no file contents, prompts or credentials, so an exported diagnostics log can be shared without leaking
/// the code under review.</remarks>
public sealed record DiagnosticEvent(string OperationId, string Stage, string? ProblemCode, double DurationMs, long Bytes = 0);
