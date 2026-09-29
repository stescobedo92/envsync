using EnvSync.Application.Diagnostics;

namespace EnvSync.Application.Tests;

public sealed class ErrorTextTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("  padded  ", "padded")]
    [InlineData("first\r\nsecond", "first second")]
    [InlineData("first\nsecond\n\n\nthird", "first second third")]
    [InlineData("tabs\tand   spaces", "tabs and spaces")]
    [InlineData(" - a\r\n - b\r\n - c", "- a - b - c")]
    [InlineData("", "")]
    public void Summarize_CollapsesEveryRunOfWhitespaceIntoOneSpace(string input, string expected)
    {
        Assert.Equal(expected, ErrorText.Summarize(input, 500));
    }

    [Fact]
    public void Summarize_LongTextIsCutAndSaysSo()
    {
        var summary = ErrorText.Summarize(new string('x', 1000), 100);

        Assert.Equal(103, summary.Length);
        Assert.EndsWith("...", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summarize_TextExactlyAtTheLimit_IsLeftAlone()
    {
        var text = new string('x', 100);

        Assert.Equal(text, ErrorText.Summarize(text, 100));
    }

    [Fact]
    public void Summarize_ControlCharactersBecomeSpacesSoTheyCannotForgeTerminalOutput()
    {
        var summary = ErrorText.Summarize("before\u001b[2Kafter\u0007bell", 500);

        Assert.DoesNotContain('\u001b', summary);
        Assert.DoesNotContain('\u0007', summary);
    }
}
