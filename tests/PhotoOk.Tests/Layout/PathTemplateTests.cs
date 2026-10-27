using PhotoOk.Core.Layout;

namespace PhotoOk.Tests.Layout;

public class PathTemplateTests
{
    [Fact]
    public void Parse_ReadsLiteralsTokensAndFormat()
    {
        var template = PathTemplate.Parse("[{기간}] {날짜:yyyyMMdd}");

        Assert.Equal(4, template.Parts.Count);
        Assert.Equal("[", template.Parts[0].Literal);
        Assert.Equal("기간", template.Parts[1].Token);
        Assert.Null(template.Parts[1].Format);
        Assert.Equal("] ", template.Parts[2].Literal);
        Assert.Equal("날짜", template.Parts[3].Token);
        Assert.Equal("yyyyMMdd", template.Parts[3].Format);
        Assert.Equal(["기간", "날짜"], template.Tokens);
    }

    [Fact]
    public void Parse_Throws_OnUnknownToken()
    {
        var ex = Assert.Throws<TemplateException>(() => PathTemplate.Parse("{분류}"));
        Assert.Contains("분류", ex.Message);
        Assert.Equal("알 수 없는 토큰: {분류}", ex.Message);
    }

    [Fact]
    public void Parse_Unescapes_DoubleBraces()
    {
        var template = PathTemplate.Parse("{{a}}");

        var part = Assert.Single(template.Parts);
        Assert.Equal("{a}", part.Literal);
        Assert.Empty(template.Tokens);
    }

    [Theory]
    [InlineData("{기기:yyyy}")]
    [InlineData("{포맷:x}")]
    public void Parse_Throws_WhenFormatOnNonDateToken(string template)
    {
        Assert.Throws<TemplateException>(() => PathTemplate.Parse(template));
    }

    [Theory]
    [InlineData("{날짜:}")]
    [InlineData("{날짜:HH}")]
    [InlineData("{연도")]
    [InlineData("연도}")]
    public void Parse_Throws_OnMalformedTemplate(string template)
    {
        Assert.Throws<TemplateException>(() => PathTemplate.Parse(template));
    }

    [Fact]
    public void KnownTokens_ContainsAllThirteen()
    {
        Assert.Equal(
            new[] { "연도", "월", "일", "날짜", "기간", "기기", "제조사", "모델", "포맷", "확장자", "원래폴더", "사유", "이벤트" }.Order(),
            PathTemplate.KnownTokens.Order());
    }
}
