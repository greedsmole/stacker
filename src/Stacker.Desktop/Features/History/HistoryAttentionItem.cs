using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed record HistoryAttentionItem(string ChangeId, string Path, string Label, bool HasConcern = false)
{
    public override string ToString() => Label;
}
