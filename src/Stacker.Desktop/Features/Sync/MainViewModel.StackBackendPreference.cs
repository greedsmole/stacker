using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private sealed record StackBackendPreference(StackBackend Backend);
}
