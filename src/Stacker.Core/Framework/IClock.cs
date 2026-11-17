using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>Reads time so commands and tests share one clock.</summary>
public interface IClock
{
    /// <summary>The current instant recorded as an entry's start and update time; the production clock reports UTC
    /// and tests substitute a fixed value so stored timestamps are deterministic.</summary>
    DateTimeOffset Now
    {
        get;
    }
}
