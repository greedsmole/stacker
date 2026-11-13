// Purpose: Syntax tokenizer scenarios for supported languages, unsupported files, limits, and fallback rendering.
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed class SyntaxTests
{
    // Scenario: TextMate colors multiline comments and themes without patch markers.
    [Fact]
    public async Task TextMate_colors_multiline_comments_and_themes_without_patch_markers()
    {
        var highlighter = new TextMateSyntaxHighlighter();
        var text = "public class Example\n{\n/* comment\nstill comment */\npublic string Name => \"hello\";\n}\n";
        var dark = await highlighter.HighlightAsync("blob", "code.cs", text, "Dark");
        Assert.NotEmpty(dark);
        Assert.NotEmpty(dark[4]);
        Assert.True(dark[5].Select(t => t.Color).Distinct().Count() > 1);
        var light = await highlighter.HighlightAsync("blob", "code.cs", text, "Light");
        Assert.NotEqual(dark[5].Select(t => t.Color), light[5].Select(t => t.Color));
        var cached = await highlighter.HighlightAsync("blob", "code.cs", text, "Dark");
        Assert.Same(dark, cached);
    }

    [Fact]
    public async Task An_absent_blob_does_not_prevent_coloring_an_added_or_deleted_file()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        await fixture.Write("Removed.cs", "public class Removed { }\n");
        await fixture.Commit("old code");
        var basis = await fixture.Git("rev-parse", "HEAD");
        File.Delete(Path.Combine(fixture.Root, "Removed.cs"));
        await fixture.Write("Added.cs", "internal static class Added { public static string Value => \"new\"; }\n");
        await fixture.Commit("replace code");
        var head = await fixture.Git("rev-parse", "HEAD");
        Assert.Null(await fixture.Reader.ReadBlobAsync(fixture.Root, basis, "Added.cs"));
        Assert.Null(await fixture.Reader.ReadBlobAsync(fixture.Root, head, "Removed.cs"));

        var files = await fixture.Reader.FilesAsync(fixture.Root, basis, head);
        using var section = new DiffSectionViewModel(fixture.Reader, fixture.Root, new("head", "base", basis, head, null, files),
            new TextMateSyntaxHighlighter());
        foreach (var file in files)
        {
            section.SelectedFile = file;
            await section.LoadAsync(file);
            Assert.NotEmpty(Assert.Single(section.Lines, row => row.IsAdded || row.IsRemoved).Tokens);
        }
    }
    // Scenario: Unknown language and oversized blobs fall back to plain text.
    [Fact]
    public async Task Unknown_language_and_oversized_blobs_fall_back_to_plain_text()
    {
        var highlighter = new TextMateSyntaxHighlighter();
        Assert.Empty(await highlighter.HighlightAsync("1", "file.unknown", "text", "Dark"));
        Assert.Empty(await highlighter.HighlightAsync("2", "file.cs", new string('x', 2 * 1024 * 1024 + 1), "Dark"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => highlighter.HighlightAsync("3", "file.cs", "public class C {}", "Dark", cts.Token));
    }
}
