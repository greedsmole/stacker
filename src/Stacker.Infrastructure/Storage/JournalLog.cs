using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure.Framework;

/// <summary>Journal entries for every repository, bounded and ordered by time.</summary>
public sealed class JournalLog
{
    public List<JournalEntry> Entries { get; set; } = [];
    public List<JournalStash> Stashes { get; set; } = [];
}
