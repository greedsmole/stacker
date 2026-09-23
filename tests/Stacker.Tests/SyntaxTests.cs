using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class SyntaxTests
{
    [Fact] public async Task TextMate_colors_multiline_comments_and_themes_without_patch_markers()
    {
        var highlighter=new TextMateSyntaxHighlighter(); var text="public class Example\n{\n/* comment\nstill comment */\npublic string Name => \"hello\";\n}\n";
        var dark=await highlighter.HighlightAsync("blob","code.cs",text,"Dark"); Assert.NotEmpty(dark); Assert.NotEmpty(dark[4]); Assert.True(dark[5].Select(t=>t.Color).Distinct().Count()>1);
        var light=await highlighter.HighlightAsync("blob","code.cs",text,"Light"); Assert.NotEqual(dark[5].Select(t=>t.Color),light[5].Select(t=>t.Color));
        var cached=await highlighter.HighlightAsync("blob","code.cs",text,"Dark"); Assert.Same(dark,cached);
    }
    [Fact] public async Task Unknown_language_and_oversized_blobs_fall_back_to_plain_text()
    {
        var highlighter=new TextMateSyntaxHighlighter(); Assert.Empty(await highlighter.HighlightAsync("1","file.unknown","text","Dark")); Assert.Empty(await highlighter.HighlightAsync("2","file.cs",new string('x',2*1024*1024+1),"Dark"));
        using var cts=new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>highlighter.HighlightAsync("3","file.cs","public class C {}","Dark",cts.Token));
    }
}
