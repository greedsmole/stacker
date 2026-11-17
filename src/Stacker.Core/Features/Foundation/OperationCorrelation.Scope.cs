using Stacker.Core.Framework;
namespace Stacker.Core;

public static partial class OperationCorrelation
{
    /// <summary>Restores the previous ambient id when the operation scope ends.</summary>
    /// <param name="end">Callback that writes the id captured by <see cref="Enter"/> back into the ambient slot.</param>
    private sealed class Scope(Action end) : IDisposable
    {
        /// <summary>Restores the previous id; every exit path from the using scope runs it, including exceptions, so an
        /// id never leaks into the caller's flow.</summary>
        public void Dispose() => end();
    }
}
