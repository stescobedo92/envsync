using EnvSync.Application.Buffers;
using EnvSync.Domain;
using EnvSync.Infrastructure.Shells;
using EnvSync.Testing;

namespace EnvSync.Infrastructure.Tests;

public sealed class CmdEmitterTests
{
    private readonly CmdEmitter _emitter = new();

    [Fact]
    public void Shell_IsCmd()
    {
        Assert.Equal(ShellKind.Cmd, _emitter.Shell);
    }

    [Theory]
    [InlineData("value", "set \"DB_PASSWORD=value\"\r\n")]
    [InlineData("with space", "set \"DB_PASSWORD=with space\"\r\n")]
    [InlineData("  padded  ", "set \"DB_PASSWORD=  padded  \"\r\n")]
    [InlineData("p&ss|w<o>r(d)", "set \"DB_PASSWORD=p&ss|w<o>r(d)\"\r\n")]
    [InlineData("a=b;c,d", "set \"DB_PASSWORD=a=b;c,d\"\r\n")]
    [InlineData("$x 'q' `t` \\s", "set \"DB_PASSWORD=$x 'q' `t` \\s\"\r\n")]
    public void TryWriteVariable_QuotesTheAssignmentSoOperatorsStayLiteral(string value, string expected)
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "DB_PASSWORD", value);

        Assert.True(accepted);
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData("100%")]
    [InlineData("%PATH%")]
    [InlineData("say \"hi\"")]
    [InlineData("wow!")]
    [InlineData("a^b")]
    [InlineData("line1\nline2")]
    [InlineData("carriage\rreturn")]
    [InlineData("nul\0char")]
    [InlineData("tab\there")]
    [InlineData("caf\u00e9")]
    [InlineData("\u65e5\u672c\u8a9e")]
    [InlineData("\u007f")]
    public void TryWriteVariable_ValuesCmdCannotCarrySafely_AreRefusedAndNothingIsWritten(string value)
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "A", value);

        Assert.False(accepted);
        Assert.Empty(output);
    }

    // cmd reads at most 8191 characters per line and treats the rest as a new command, so a hostile value that pads a line past the
    // limit could smuggle its tail in as code. A statement that long is refused instead.
    [Fact]
    public void TryWriteVariable_TheLongestStatementCmdCanReadSafelyIsAcceptedAndOneMoreCharacterIsRefused()
    {
        // 'set "A=' + value + '"' + CRLF is 10 characters longer than the value; the cap is 8000.
        var (acceptedAtLimit, atLimit) = EmitterAssert.Emit(_emitter, "A", new string('x', 7990));
        var (acceptedOver, overLimit) = EmitterAssert.Emit(_emitter, "A", new string('x', 7991));

        Assert.True(acceptedAtLimit);
        Assert.Equal(8000, atLimit.Length);
        Assert.False(acceptedOver);
        Assert.Empty(overLimit);
    }

    [Fact]
    public void TryWriteVariable_AValueThatPadsPastTheLineLimitToSmuggleACommand_IsRefused()
    {
        var hostile = new string('x', 8185) + "& echo SMUGGLED &";

        var (accepted, output) = EmitterAssert.Emit(_emitter, "A", hostile);

        Assert.False(accepted);
        Assert.Empty(output);
    }

    [Fact]
    public void WriteFailure_SetsTheErrorLevelWithoutClosingTheConsole()
    {
        using var writer = new PooledCharBufferWriter(64);

        _emitter.WriteFailure(writer, 11);

        Assert.Equal("cmd /c exit 11\r\n", writer.WrittenSpan.ToString());
    }

    [Fact]
    public void TryWriteVariable_DoesNotAllocate()
    {
        using var writer = new PooledCharBufferWriter(4096);
        var name = EmitterAssert.Name("DB_PASSWORD");
        const string value = "p&ss|w<o>r(d) with 'quotes'";

        var allocated = AllocationProbe.Measure(() => _emitter.TryWriteVariable(name, value, writer));

        Assert.Equal(0, allocated);
    }
}
