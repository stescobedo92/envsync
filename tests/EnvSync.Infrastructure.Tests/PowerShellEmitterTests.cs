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
    [InlineData("ünïcödé 日本語", "$env:DB_PASSWORD = 'ünïcödé 日本語'\n")]
    public void TryWriteVariable_SingleQuotesTheValueSoNothingIsInterpreted(string value, string expected)
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

    [Theory]
    [InlineData("‘", "$env:A = '‘‘'\n")]
    [InlineData("’", "$env:A = '’’'\n")]
    [InlineData("‚", "$env:A = '‚‚'\n")]
    [InlineData("‛", "$env:A = '‛‛'\n")]
    [InlineData("‘x’; calc; ‘", "$env:A = '‘‘x’’; calc; ‘‘'\n")]
    public void TryWriteVariable_DoublesTheTypographicQuotesPowerShellTreatsAsSingleQuotes(string value, string expected)
    {
        var (accepted, output) = EmitterAssert.Emit(_emitter, "A", value);

        Assert.True(accepted);
        Assert.Equal(expected, output);
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
        const string value = "it's a $ecret\nwith ‘quotes’";

        var allocated = AllocationProbe.Measure(() => _emitter.TryWriteVariable(name, value, writer));

        Assert.Equal(0, allocated);
    }
}
