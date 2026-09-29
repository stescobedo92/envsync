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
    public void Sanitize_ReplacesEveryCharacterThatCouldForgeOutput_AndLeavesTheRestAlone()
    {
        // ESC, line and paragraph separators, bidirectional overrides and zero-width joiners
        var hostile = "a\x1b[2Kb\x2028c\x2029d\x202Ee\x2066f\x200Bg\nh\ri";

        var clean = ErrorText.Sanitize(hostile);

        Assert.Equal("a?[2Kb?c?d?e?f?g?h?i", clean);
        Assert.Equal("plain text - ünïcödé 日本語", ErrorText.Sanitize("plain text - ünïcödé 日本語"));
    }

    [Fact]
    public void Sanitize_NothingToChange_ReturnsTheSameInstance()
    {
        var text = "DB_PASSWORD";

        Assert.Same(text, ErrorText.Sanitize(text));
    }

    [Fact]
    public void Summarize_ControlCharactersBecomeSpacesSoTheyCannotForgeTerminalOutput()
    {
        var summary = ErrorText.Summarize("before\u001b[2Kafter\u0007bell", 500);

        Assert.DoesNotContain('\u001b', summary);
        Assert.DoesNotContain('\u0007', summary);
    }
}
