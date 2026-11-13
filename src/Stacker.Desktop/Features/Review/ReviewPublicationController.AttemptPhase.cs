using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class ReviewPublicationController
{
    private enum AttemptPhase
    {
        BeforeDispatch, Dispatching, Acknowledged
    }
}
