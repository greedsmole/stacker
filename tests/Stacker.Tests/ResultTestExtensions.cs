using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Tests;

internal static class ResultTestExtensions
{
    /// <summary>Test helper: the message a failing OrThrow would produce.</summary>
    public static string OrThrowExceptionMessage<T>(this Result<T> result)
    {
        try
        {
            result.OrThrow();
            return "";
        }
        catch (StackerException ex) { return ex.Message; }
    }
}
