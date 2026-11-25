using Avalonia.Media;
using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public sealed partial class WorkingTreeViewModel
{
    private async Task LoadPatchAsync(WorkingTreeFile? file)
    {
        _patch?.Cancel();
        Lines = [];
        PatchMessage = file is null ? "Select a file to inspect its changes." : "";
        if (file is null || _snapshot is not { } snapshot || !Owner.IsWorkingTreeOpen)
        {
            IsLoadingPatch = false;
            return;
        }
        var load = new CancellationTokenSource();
        _patch = load;
        IsLoadingPatch = true;
        try
        {
            var result = await Authoring.ReadPatchAsync(snapshot, file, load.Token);
            if (!IsCurrent())
                return;
            if (result.IsFailed)
            {
                PatchMessage = result.Problem!.Message;
                return;
            }
            var (rows, empty) = await Task.Run(() =>
            {
                var diff = DiffView.From(result.Value!);
                var rows = new RenderedDiffLine[diff.Rows.Count];
                for (var i = 0; i < rows.Length; i++)
                {
                    if (i % 1024 == 0)
                        load.Token.ThrowIfCancellationRequested();
                    rows[i] = new(diff.Rows[i]) { AllowComment = false };
                }
                return (rows, diff.EmptyReason);
            }, load.Token);
            if (IsCurrent())
            {
                if (Owner.Settings.WrapCode)
                    foreach (var row in rows)
                        row.TextWrapping = TextWrapping.Wrap;
                Lines = rows;
                PatchMessage = empty;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrent())
                PatchMessage = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_patch, load))
            {
                _patch = null;
                IsLoadingPatch = false;
            }
            load.Dispose();
        }

        bool IsCurrent() => !load.IsCancellationRequested && ReferenceEquals(_patch, load) &&
            ReferenceEquals(_snapshot, snapshot) && Owner.IsWorkingTreeOpen && Owner.Repository?.Root == snapshot.Root;
    }
}
