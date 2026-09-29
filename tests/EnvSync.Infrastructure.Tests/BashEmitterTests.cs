using EnvSync.Application.Buffers;
using EnvSync.Domain;
using EnvSync.Infrastructure.Shells;
using EnvSync.Testing;

namespace EnvSync.Infrastructure.Tests;

public sealed class BashEmitterTests
{
    private readonly BashEmitter _emitter = new();

    [Fact]
    public void Shell_IsBash()
    {
        Assert.Equal(ShellKind.Bash, _emitter.Shell);
    }

    [Theory]
    [InlineData("value", "export DB_PASSWORD='value'\n")]
    [InlineData("", "export DB_PASSWORD=''\n")]
    [InlineData("with space", "export DB_PASSWORD='with space'\n")]
    [InlineData("$(id)", "export DB_PASSWORD='$(id)'\n")]
    [InlineData("`id`", "export DB_PASSWORD='`id`'\n")]
    [InlineData("$HOME;rm -rf ~", "export DB_PASSWORD='$HOME;rm -rf ~'\n")]
    [InlineData("say \"hi\"", "export DB_PASSWORD='say \"hi\"'\n")]
    [InlineData("back\\slash", "export DB_PASSWORD='back\\slash'\n")]
    [InlineData("line1\nline2", "export DB_PASSWORD='line1\nline2'\n")]
    [InlineData("ünïcödé 日本語", "export DB_PASSWORD='ünïcödé 日本語'\n")]
    public void TryWriteVariable_SingleQuotesTheValueSoNothingIsInterpreted(string value, string expected)
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "DB_PASSWORD", value);

        Assert.True(accepted);
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData("it's", "export DB_PASSWORD='it'\\''s'\n")]
    [InlineData("'", "export DB_PASSWORD=''\\'''\n")]
    [InlineData("a''b", "export DB_PASSWORD='a'\\'''\\''b'\n")]
    public void TryWriteVariable_EscapesEmbeddedSingleQuotes(string value, string expected)
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "DB_PASSWORD", value);

        Assert.True(accepted);
        Assert.Equal(expected, output);
    }

    // Cygwin/MSYS bash (Git Bash on Windows) drops a raw CR while reading a script, even inside single quotes.
    // Writing it as the ANSI-C escape $'\r' is plain ASCII, so no way of reading the script can lose it.
    [Theory]
    [InlineData("a\rb", "export DB_PASSWORD='a'$'\\r''b'\n")]
    [InlineData("\r", "export DB_PASSWORD=''$'\\r'''\n")]
    [InlineData("a\r\nb", "export DB_PASSWORD='a'$'\\r''\nb'\n")]
    [InlineData("it's\r", "export DB_PASSWORD='it'\\''s'$'\\r'''\n")]
    public void TryWriteVariable_WritesCarriageReturnsAsAnsiCEscapes(string value, string expected)
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "DB_PASSWORD", value);

        Assert.True(accepted);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void TryWriteVariable_ValueWithNul_IsRefusedAndNothingIsWritten()
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "A", "bad\0value");

        Assert.False(accepted);
        Assert.Empty(output);
    }

    [Fact]
    public void WriteFailure_MakesTheConsumerFailWithTheExitCodeWithoutClosingAnInteractiveShell()
    {
        using var writer = new PooledCharBufferWriter(64);

        _emitter.WriteFailure(writer, 11);

        Assert.Equal("(exit 11)\n", writer.WrittenSpan.ToString());
    }

    [Fact]
    public void TryWriteVariable_EmitsOneStatementPerCall()
    {
        using var writer = new PooledCharBufferWriter(256);

        _emitter.TryWriteVariable(EmitterAssert.Name("A"), "1", writer);
        _emitter.TryWriteVariable(EmitterAssert.Name("B"), "2", writer);

        Assert.Equal("export A='1'\nexport B='2'\n", writer.WrittenSpan.ToString());
    }

    [Fact]
    public void TryWriteVariable_DoesNotAllocate()
    {
        using var writer = new PooledCharBufferWriter(4096);
        var name = EmitterAssert.Name("DB_PASSWORD");
        const string value = "it's a $ecret with 'quotes'";

        var allocated = AllocationProbe.Measure(() => _emitter.TryWriteVariable(name, value, writer));

        Assert.Equal(0, allocated);
    }
}
