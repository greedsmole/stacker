using Stacker.Core;

namespace Stacker.Tests;

public sealed class ReviewContentTests
{
    [Fact]
    public void Suggestions_preserve_prose_code_order_indentation_and_empty_deletions()
    {
        var blocks = ReviewContent.Parse("Explain why\n```suggestion\n    return true;\n```\nThen remove this\n```suggestion\n```\nAfter");
        Assert.Equal(5, blocks.Count);
        Assert.Equal("Explain why", blocks[0].Text);
        Assert.Equal("    return true;", blocks[1].Text); Assert.True(blocks[1].IsSuggestion);
        Assert.Equal("Then remove this", blocks[2].Text);
        Assert.True(blocks[3].IsDeletion);
        Assert.Equal("After", blocks[4].Text);
    }
    [Theory]
    [InlineData("```suggestion\nnot closed")]
    [InlineData("````text\n```suggestion\nexample\n```\n````")]
    [InlineData("```csharp\nreturn true;\n```")]
    [InlineData("    ```suggestion\nnot a fence\n    ```")]
    public void Ordinary_code_examples_and_incomplete_fences_stay_plain_text(string body)
    {
        var block = Assert.Single(ReviewContent.Parse(body));
        Assert.False(block.IsSuggestion); Assert.Equal(body, block.Text);
    }
    [Fact]
    public void GitLab_offsets_and_CRLF_are_recognized_without_losing_code()
    {
        var block = Assert.Single(ReviewContent.Parse("```suggestion:-2+3\r\n  replacement\r\n```"));
        Assert.True(block.IsSuggestion); Assert.Equal(2, block.LinesAbove); Assert.Equal(3, block.LinesBelow);
        Assert.Equal("  replacement", block.Text);
    }
    [Theory]
    [InlineData("one\ntwo")]
    [InlineData("one\n")]
    [InlineData("```suggestion\nembedded Markdown\n```")]
    [InlineData("")]
    public void Generated_suggestions_round_trip_with_backticks_and_deletion(string code)
    {
        var block = Assert.Single(ReviewContent.Parse(ReviewContent.Suggestion(code)));
        Assert.True(block.IsSuggestion); Assert.Equal(code, block.Text);
    }
    [Fact]
    public void Context_comments_use_right_side_and_deleted_comments_use_left()
    {
        var pr = DiscoveryTests.Pr(1, "main", "feature");
        var context = new DiffLine(DiffLineKind.Context, 10, 12, " same");
        Assert.True(ReviewSelection.TryCreateAnchor(pr, "a.cs", [context], null, out var anchor, out _));
        Assert.Equal("RIGHT", anchor!.Side); Assert.Equal(12, anchor.Line);
        Assert.False(ReviewSelection.TryCreateAnchor(pr, "a.cs", [context], "LEFT", out _, out _));
        Assert.True(ReviewSelection.TryCreateAnchor(pr, "a.cs", [new(DiffLineKind.Removed, 10, null, "-old")], null, out anchor, out _));
        Assert.Equal("LEFT", anchor!.Side);
    }
    [Fact]
    public void Invalid_gaps_headers_and_mixed_sides_cannot_be_comment_ranges()
    {
        var pr = DiscoveryTests.Pr(1, "main", "feature");
        Assert.False(ReviewSelection.TryCreateAnchor(pr, "a.cs", [new(DiffLineKind.Added, null, 1, "+a"), new(DiffLineKind.Added, null, 3, "+c")], null, out _, out _));
        Assert.False(ReviewSelection.TryCreateAnchor(pr, "a.cs", [new(DiffLineKind.Removed, 1, null, "-a"), new(DiffLineKind.Added, null, 1, "+a")], null, out _, out _));
        Assert.False(ReviewSelection.TryCreateAnchor(pr, "a.cs", [new(DiffLineKind.Header, null, null, "@@")], null, out _, out _));
    }
}
