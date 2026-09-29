using EnvSync.Application.Buffers;
using EnvSync.Domain;
using EnvSync.Infrastructure.Shells;
using EnvSync.Testing;

namespace EnvSync.Infrastructure.Tests;

public sealed class PowerShellEmitterTests
{
    private readonly PowerShellEmitter _emitter = new();

    [Fact]
    public void Shell_IsPowerShell()
    {
        Assert.Equal(ShellKind.PowerShell, _emitter.Shell);
    }

    [Theory]
    [InlineData("value", "$env:DB_PASSWORD = 'value'\n")]
    [InlineData("", "$env:DB_PASSWORD = ''\n")]
    [InlineData("with space", "$env:DB_PASSWORD = 'with space'\n")]
    [InlineData("$(Get-Date) $env:PATH", "$env:DB_PASSWORD = '$(Get-Date) $env:PATH'\n")]
    [InlineData("`n `t backtick", "$env:DB_PASSWORD = '`n `t backtick'\n")]
    [InlineData("say \"hi\"", "$env:DB_PASSWORD = 'say \"hi\"'\n")]
    [InlineData("back\\slash", "$env:DB_PASSWORD = 'back\\slash'\n")]
    public void TryWriteVariable_SingleQuotesPrintableAsciiSoNothingIsInterpreted(string value, string expected)
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "DB_PASSWORD", value);

        Assert.True(accepted);
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData("it's", "$env:DB_PASSWORD = 'it''s'\n")]
    [InlineData("'", "$env:DB_PASSWORD = ''''\n")]
    public void TryWriteVariable_DoublesEmbeddedSingleQuotes(string value, string expected)
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "DB_PASSWORD", value);

        Assert.True(accepted);
        Assert.Equal(expected, output);
    }

    // PowerShell decodes a native command's stdout with the console code page (437, 850, 1252...), not UTF-8, so any byte above
    // 0x7F can be silently changed on the way in, and under some code pages can even end a quoted string. The script is therefore
    // pure ASCII: every other character is written as its UTF-16 code unit.
    [Theory]
    [InlineData("é", "$env:A = '' + [char]233 + ''\n")]
    [InlineData("café", "$env:A = 'caf' + [char]233 + ''\n")]
    [InlineData("ünï", "$env:A = '' + [char]252 + 'n' + [char]239 + ''\n")]
    [InlineData("日", "$env:A = '' + [char]26085 + ''\n")]
    [InlineData("Ñ;calc;#", "$env:A = '' + [char]209 + ';calc;#'\n")]
    [InlineData("‘", "$env:A = '' + [char]8216 + ''\n")]
    [InlineData("’", "$env:A = '' + [char]8217 + ''\n")]
    [InlineData("‚", "$env:A = '' + [char]8218 + ''\n")]
    [InlineData("‛", "$env:A = '' + [char]8219 + ''\n")]
    [InlineData("\U0001F600", "$env:A = '' + [char]55357 + '' + [char]56832 + ''\n")]
    [InlineData("tab\there", "$env:A = 'tab' + [char]9 + 'here'\n")]
    [InlineData("\u007f", "$env:A = '' + [char]127 + ''\n")]
    [InlineData("\x0085\x2028\x2029", "$env:A = '' + [char]133 + '' + [char]8232 + '' + [char]8233 + ''\n")]
    public void TryWriteVariable_WritesEveryNonPrintableAsciiCharacterAsItsCodeUnit(string value, string expected)
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "A", value);

        Assert.True(accepted);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void TryWriteVariable_TheOutputIsAlwaysPureAsciiWhateverTheValue()
    {
        var everything = string.Concat(Enumerable.Range(1, 0x2FF).Select(i => (char)i)) + "\U0001F600 日本語 ‘’";

        var (accepted, output) = EmitterAssert.Emit(_emitter, "A", everything);

        Assert.True(accepted);
        Assert.All(output, c => Assert.True(c <= '~' && (c >= ' ' || c == '\n'), $"U+{(int)c:X4} is not printable ASCII"));
    }

    [Theory]
    [InlineData("a\nb", "$env:A = 'a' + [char]10 + 'b'\n")]
    [InlineData("a\r\nb", "$env:A = 'a' + [char]13 + '' + [char]10 + 'b'\n")]
    [InlineData("a\n", "$env:A = 'a' + [char]10 + ''\n")]
    [InlineData("\nb", "$env:A = '' + [char]10 + 'b'\n")]
    [InlineData("\n", "$env:A = '' + [char]10 + ''\n")]
    public void TryWriteVariable_KeepsEachStatementOnOneLineByEncodingLineBreaks(string value, string expected)
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "A", value);

        Assert.True(accepted);
        Assert.Equal(expected, output);
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void WriteFailure_ThrowsSoAnInvokeExpressionPipelineStopsAndSaysWhy()
    {
        using var writer = new PooledCharBufferWriter(128);

        _emitter.WriteFailure(writer, 11);

        Assert.Equal("throw 'envsync: could not set the environment (exit code 11); see the messages above.'\n", writer.WrittenSpan.ToString());
    }

    [Fact]
    public void TryWriteVariable_ValueWithNul_IsRefusedAndNothingIsWritten()
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "A", "bad\0value");

        Assert.False(accepted);
        Assert.Empty(output);
    }

    [Fact]
    public void TryWriteVariable_DoesNotAllocate()
    {
        using var writer = new PooledCharBufferWriter(4096);
        var name = EmitterAssert.Name("DB_PASSWORD");
        const string value = "it's a $ecret\nwith ‘quotes’ and 日本語";

        var allocated = AllocationProbe.Measure(() => _emitter.TryWriteVariable(name, value, writer));

        Assert.Equal(0, allocated);
    }
}
