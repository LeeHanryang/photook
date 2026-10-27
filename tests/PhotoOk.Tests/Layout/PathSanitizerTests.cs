using PhotoOk.Core.Layout;

namespace PhotoOk.Tests.Layout;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("NIKON Z f ", "NIKON Z f")]
    [InlineData("A/B", "A_B")]
    [InlineData("CON", "CON_")]
    [InlineData("x.", "x")]
    [InlineData("   ", "_")]
    [InlineData("a:b*c?d", "a_b_c_d")]
    [InlineData("con", "con_")]
    [InlineData("Com3", "Com3_")]
    [InlineData("LPT9.txt", "LPT9.txt_")]
    [InlineData("COM0", "COM0")]
    [InlineData("CONSOLE", "CONSOLE")]
    [InlineData("..", "_")]
    [InlineData(" x . ", "x")]
    [InlineData("", "_")]
    public void SanitizeSegment_(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeSegment(input));
    }
}
