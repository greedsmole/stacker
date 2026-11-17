using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>A settings store that can also report the problem from its most recent load or save.</summary>
/// <remarks>Implemented beside the settings port rather than inside it, so a caller that only reads settings stays
/// unaffected. The navigation controller checks it after loading settings and surfaces the message once.</remarks>
public interface ISettingsDiagnostics
{
    /// <summary>The most recent settings problem, or <see langword="null"/> when none was recorded.</summary>
    /// <remarks>Sticky: it stays set until a later failing load or save replaces it. It is set when a damaged
    /// document was quarantined and defaults were used, and when a write failed. An unsupported future schema is
    /// reported by the load itself and blocks writes, so that case does not appear here.</remarks>
    string? LastProblem
    {
        get;
    }
}
