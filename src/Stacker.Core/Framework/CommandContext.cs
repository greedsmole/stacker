using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>Everything a command may use while it runs; effects go through the journal.</summary>
/// <remarks>The runner creates one context per command after opening the entry. <see cref="Entry"/> is the durable
/// record and <see cref="Journal"/> is the supported way to advance it; <see cref="Progress"/> receives short
/// status lines (for example, which layer is being rebased), not errors.</remarks>
/// <param name="Entry">The running journal entry opened for this command.</param>
/// <param name="Journal">The journal the command records its steps through.</param>
/// <param name="Progress">Optional sink for short progress text.</param>
public sealed record CommandContext(JournalEntry Entry, IJournal Journal, IProgress<string>? Progress);
