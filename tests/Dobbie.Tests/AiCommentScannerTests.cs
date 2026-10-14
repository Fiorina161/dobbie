namespace Dobbie.Tests;

public class AiCommentScannerTests
{
    private static IReadOnlyList<AiComment> Cs(params string[] lines)
        => AiCommentScanner.ScanLines("Test.cs", lines);

    private static IReadOnlyList<AiComment> Vb(params string[] lines)
        => AiCommentScanner.ScanLines("Test.vb", lines);

    [Fact]
    public void TrailingBang_IsCommand()
    {
        var c = Assert.Single(Cs("// make this method async AI!"));
        Assert.Equal(AiCommentKind.Command, c.Kind);
        Assert.Equal("make this method async AI!", c.Text);
        Assert.Equal(1, c.LineNumber);
    }

    [Fact]
    public void LeadingBang_NoSpaceAfterMarker_IsCommand()
    {
        var c = Assert.Single(Cs("//AI! rename the variable"));
        Assert.Equal(AiCommentKind.Command, c.Kind);
    }

    [Fact]
    public void TrailingQuestion_IsQuestion()
    {
        var c = Assert.Single(Cs("// why does this test fail AI?"));
        Assert.Equal(AiCommentKind.Question, c.Kind);
    }

    [Fact]
    public void BareAi_IsContext()
    {
        var c = Assert.Single(Cs("// AI"));
        Assert.Equal(AiCommentKind.Context, c.Kind);
    }

    [Fact]
    public void LeadingPlainAi_IsContext()
    {
        var c = Assert.Single(Cs("// AI keep the cache lazy here"));
        Assert.Equal(AiCommentKind.Context, c.Kind);
    }

    [Fact]
    public void TrailingCommentAfterCode_IsFound()
    {
        var c = Assert.Single(Cs("int x = 1; // rename this AI!"));
        Assert.Equal(AiCommentKind.Command, c.Kind);
        Assert.Equal("rename this AI!", c.Text);
    }

    [Fact]
    public void UrlInsideStringLiteral_IsNotAComment()
    {
        Assert.Empty(Cs("var url = \"http://example.com/AI!\";"));
    }

    [Fact]
    public void UrlInsideComment_KeepsFullText()
    {
        var c = Assert.Single(Cs("// see http://example.com AI!"));
        Assert.Equal("see http://example.com AI!", c.Text);
    }

    [Fact]
    public void AiAsPartOfWord_DoesNotMatch()
    {
        Assert.Empty(Cs("// aim for the stars"));
        Assert.Empty(Cs("// we need to repair this"));
    }

    [Fact]
    public void AiInMiddleOfSentence_DoesNotMatch()
    {
        Assert.Empty(Cs("// is AI? the future of coding"));
    }

    [Fact]
    public void CaseInsensitive()
    {
        var c = Assert.Single(Cs("// do it ai!"));
        Assert.Equal(AiCommentKind.Command, c.Kind);
    }

    [Fact]
    public void CharLiteralWithSlash_DoesNotStartComment()
    {
        Assert.Empty(Cs("var a = '/'; var b = '/';"));
    }

    [Fact]
    public void CommentAfterCharLiteral_IsFound()
    {
        var c = Assert.Single(Cs("var a = '/'; // simplify AI!"));
        Assert.Equal("simplify AI!", c.Text);
    }

    [Fact]
    public void LineNumbers_AreOneBased()
    {
        var comments = Cs(
            "class C",
            "{",
            "    // refactor this AI!",
            "}");
        var c = Assert.Single(comments);
        Assert.Equal(3, c.LineNumber);
    }

    [Fact]
    public void Vb_Comment_IsFound()
    {
        var c = Assert.Single(Vb("' convert this loop to LINQ AI!"));
        Assert.Equal(AiCommentKind.Command, c.Kind);
        Assert.Equal("convert this loop to LINQ AI!", c.Text);
    }

    [Fact]
    public void Vb_ApostropheInsideString_IsNotAComment()
    {
        Assert.Empty(Vb("Dim s = \"it's fine\""));
    }

    [Fact]
    public void Vb_CommentAfterStringWithApostrophe_HasCleanText()
    {
        var c = Assert.Single(
            Vb("Dim s = \"it's\" ' fix the null check AI?"));
        Assert.Equal(AiCommentKind.Question, c.Kind);
        Assert.Equal("fix the null check AI?", c.Text);
    }

    [Fact]
    public void Vb_DoubledQuoteEscape_IsHandled()
    {
        Assert.Empty(Vb("Dim s = \"say \"\"hi AI!\"\" now\""));
    }

    [Fact]
    public void CSharp_EscapedQuoteInString_IsHandled()
    {
        Assert.Empty(Cs("var s = \"quote \\\" // AI!\";"));
    }

    [Fact]
    public void MultipleFilesKinds_AllFound()
    {
        var comments = Cs(
            "// AI",
            "int x; // speed this up AI!",
            "// what does this do AI?");
        Assert.Equal(3, comments.Count);
        Assert.Equal(AiCommentKind.Context, comments[0].Kind);
        Assert.Equal(AiCommentKind.Command, comments[1].Kind);
        Assert.Equal(AiCommentKind.Question, comments[2].Kind);
    }
}
