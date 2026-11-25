using System.Collections.ObjectModel;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class DiffSectionViewModel
{
    internal Task PatchReady { get; private set; } = Task.CompletedTask;
    /// <summary>Loads one file's patch on demand and cancels any request for the previously selected file.</summary>
    /// <remarks>Results are applied only while this request remains current, preventing stale selections from flashing.</remarks>
    public async Task LoadAsync(FileChange? file)
    {
        _load?.Cancel();
        var load = new CancellationTokenSource();
        _load = load;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PatchReady = ready.Task;
        ResetContext();
        Lines.Clear();
        FileThreads.Clear();
        Patch = "";
        HasLoadError = false;
        IsBusy = file is not null;
        Message = file is null ? "Select a file to view changes." : "Loading diff…";
        try
        {
            if (file is null || _disposed)
                return;
            var diff = (await _git.PatchAsync(_root, Result.BaseSha, Result.HeadSha, file, load.Token)).OrThrow();
            if (load.IsCancellationRequested || _disposed)
                return;
            Patch = diff.Patch;
            var readContext = _git is IBlobReader && DiffContext.CanRead(file);
            var highlight = _highlighter is not null && !file.IsBinary && !file.IsSubmodule;
            var wrapping = WrapCode ? TextWrapping.Wrap : TextWrapping.NoWrap;
            var prepared = await Task.Run(() =>
            {
                var presentation = DiffView.From(diff);
                var rows = new RenderedDiffLine[presentation.Rows.Count];
                for (var i = 0; i < rows.Length; i++)
                {
                    if (i % 1024 == 0)
                        load.Token.ThrowIfCancellationRequested();
                    rows[i] = new(presentation.Rows[i])
                    {
                        AllowComment = !IsReadOnlyComparison,
                        TextWrapping = wrapping
                    };
                }
                return (presentation, rows);
            }, load.Token);
            if (load.IsCancellationRequested || _disposed)
                return;
            var view = prepared.presentation;
            if (wrapping != (WrapCode ? TextWrapping.Wrap : TextWrapping.NoWrap))
                foreach (var row in prepared.rows)
                    row.TextWrapping = WrapCode ? TextWrapping.Wrap : TextWrapping.NoWrap;
            Lines.ReplaceAll(prepared.rows);
            Message = Lines.Count == 0 ? view.EmptyReason : "";
            IsBusy = false;
            if (Threads.Count > 0 || Review?.DraftComments.Count > 0)
                ApplyThreads();
            ready.TrySetResult();

            var head = readContext || (highlight && diff.Hunks.Any(hunk => hunk.NewCount > 0))
                ? await ReadSourceBlobAsync(Result.HeadSha, file.NewPath, load.Token)
                : null;
            if (readContext)
            {
                var context = head is null ? null
                    : await Task.Run(() => DiffContext.Create(diff, view, head.Text, load.Token), load.Token);
                load.Token.ThrowIfCancellationRequested();
                SetContext(context, prepared.rows, head is null);
            }
            if (highlight)
                await HighlightAsync(file, head, load.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!load.IsCancellationRequested && !_disposed) { HasLoadError = true; Message = ex.Message; } }
        finally
        {
            if (ReferenceEquals(_load, load)) { _load = null; IsBusy = false; }
            ready.TrySetResult();
            load.Dispose();
        }
    }
    // Old and new blobs are tokenized separately so multiline grammar state follows each side's actual source text.
    private async Task HighlightAsync(FileChange file, BlobContent? head, CancellationToken ct)
    {
        if (_highlighter is null)
            return;
        var old = Lines.Any(line => line.IsRemoved) ? await ReadSourceBlobAsync(Result.BaseSha, file.OldPath, ct) : null;
        var oldTokens = await ReadTokensAsync(old, file.OldPath);
        var newTokens = await ReadTokensAsync(head, file.NewPath);
        ct.ThrowIfCancellationRequested();
        _contextTokens = _context is null ? null : newTokens;
        foreach (var line in Lines)
        {
            var number = line.IsRemoved ? line.OldLineNumber : line.NewLineNumber;
            var source = line.IsRemoved ? oldTokens : newTokens;
            if (number is int index && source?.TryGetValue(index, out var spans) == true)
                line.Tokens = spans;
        }

        async Task<IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>>?> ReadTokensAsync(BlobContent? content, string path)
        {
            if (content is null)
                return null;
            try
            {
                var tokens = await _highlighter.HighlightAsync(content.Id, path, content.Text, Theme, ct);
                return tokens.IsFailed ? null : tokens.Value;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
    }
    private async Task<BlobContent?> ReadSourceBlobAsync(string commit, string path, CancellationToken ct)
    {
        if (_git is not IBlobReader reader)
            return null;
        try
        {
            var result = await reader.ReadBlobAsync(_root, commit, path, ct);
            return result.IsFailed ? null : result.Value;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
