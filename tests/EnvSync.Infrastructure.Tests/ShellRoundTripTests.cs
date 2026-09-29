using EnvSync.Application.Abstractions;
using EnvSync.Application.Buffers;
using EnvSync.Infrastructure.Shells;

namespace EnvSync.Infrastructure.Tests;

/// <summary>
/// The escaping rules are only worth something if the real shell reads back exactly what was written.
/// Each test runs every hostile value through one real shell process and compares the result byte for byte.
/// Tests are skipped, never faked, when a shell is not installed on the machine running them.
/// </summary>
public sealed class ShellRoundTripTests
{
    private const string Prefix = "ENVSYNC_RT_";

    // Anything that would run code, expand, split or truncate if a shell interpreted it instead of storing it.
    // The HACKED payloads would create a file in the working directory if they were ever executed.
    private static readonly string[] Hostile =
    [
        "simple",
        "with space",
        "  leading and trailing  ",
        "it's",
        "''",
        "'; touch HACKED; '",
        "$(touch HACKED)",
        "`touch HACKED`",
        "\"; New-Item HACKED; \"",
        "$(New-Item HACKED)",
        "'; New-Item HACKED; '",
        "& echo pwned > HACKED",
        "| echo pwned > HACKED",
        "\"double quotes\"",
        "back\\slash \\n \\t",
        "$HOME ${HOME} $env:HOME $PATH",
        "!bang !! ^caret %percent%",
        "#not-a-comment",
        "; echo HACKED",
        "&& echo HACKED",
        "-n",
        "*",
        "~",
        "tab\tinside",
        "line1\nline2",
        "line1\r\nline2",
        "ends-with-newline\n",
        "\nstarts-with-newline",
        "\n\n",
        "unicode é ñ 日本語 😀",
        "‘curly’ “double” ‚low‛",
        "mixed 'single' \"double\" `tick` $dollar",
        string.Empty,
    ];

    // The subset cmd can carry safely: printable ASCII without % " ! ^ (the emitter refuses everything else).
    private static readonly string[] CmdSafe =
    [
        "simple",
        "with space",
        "  padded  ",
        "p&ss|w<o>r(d)",
        "& echo pwned > HACKED",
        "| echo pwned > HACKED",
        "a=b;c,d",
        "=leading-equals",
        "#hash $dollar 'single' `tick`",
        "back\\slash ..\\..\\x",
        "wild*card? ~tilde",
    ];

    private static (string Script, string[] Names) Build(IShellEmitter emitter, string[] values)
    {
        using var writer = new PooledCharBufferWriter(4096);
        var names = new string[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            names[i] = Prefix + i;
            var accepted = emitter.TryWriteVariable(EmitterAssert.Name(names[i]), values[i], writer);
            Assert.True(accepted, $"The emitter refused a value it should carry: {Describe(values[i])}");
        }

        return (writer.WrittenSpan.ToString(), names);
    }

    private static string Describe(string value) =>
        "\"" + value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    private static void AssertRoundTrip(string[] expected, IReadOnlyList<string> actual, string workingDirectory)
    {
        var mismatches = new List<string>();

        if (actual.Count != expected.Length)
        {
            mismatches.Add($"Expected {expected.Length} values back but the shell returned {actual.Count}.");
        }

        for (var i = 0; i < Math.Min(expected.Length, actual.Count); i++)
        {
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
            {
                mismatches.Add($"  sent {Describe(expected[i])}{Environment.NewLine}  got  {Describe(actual[i])}");
            }
        }

        Assert.True(mismatches.Count == 0, "Values did not survive the shell:" + Environment.NewLine + string.Join(Environment.NewLine, mismatches));
        Assert.Empty(Directory.GetFileSystemEntries(workingDirectory));
    }

    private static string FailureScript(IShellEmitter emitter, int exitCode)
    {
        using var writer = new PooledCharBufferWriter(256);
        emitter.WriteFailure(writer, exitCode);
        return writer.WrittenSpan.ToString();
    }

    [Fact]
    public async Task Bash_TheFailureScript_StopsAScriptThatEvalsItUnderSetEAndKeepsTheExitCode()
    {
        var bash = ShellHarness.FindBash();
        Assert.SkipUnless(bash is not null, "bash is not installed on this machine.");
        using var cwd = new TempDirectory();
        var failure = FailureScript(new BashEmitter(), 11).TrimEnd('\n');

        var (exitCode, stdout) = await ShellHarness.RunBashScriptAsync(
            bash, $"set -e\nscript=$(printf '%s' '{failure}')\neval \"$script\"\necho AFTER\n", cwd.Path, TestContext.Current.CancellationToken);

        Assert.Equal(11, exitCode);
        Assert.DoesNotContain("AFTER", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerShell7_TheFailureScript_StopsThePipelineThatInvokesIt()
    {
        var pwsh = ShellHarness.FindPwsh();
        Assert.SkipUnless(pwsh is not null, "PowerShell 7 (pwsh) is not installed on this machine.");
        using var cwd = new TempDirectory();

        var (exitCode, stdout) = await ShellHarness.RunPowerShellScriptAsync(
            pwsh, FailureScript(new PowerShellEmitter(), 11), cwd.Path, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("AFTER", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsPowerShell51_TheFailureScript_StopsThePipelineThatInvokesIt()
    {
        var powershell = ShellHarness.FindWindowsPowerShell();
        Assert.SkipUnless(powershell is not null, "Windows PowerShell 5.1 exists only on Windows.");
        using var cwd = new TempDirectory();

        var (exitCode, stdout) = await ShellHarness.RunPowerShellScriptAsync(
            powershell, FailureScript(new PowerShellEmitter(), 11), cwd.Path, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("AFTER", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cmd_TheFailureScript_LeavesTheErrorLevelAtTheExitCode()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe exists only on Windows.");
        using var scriptDir = new TempDirectory();
        using var cwd = new TempDirectory();

        var (_, stdout) = await ShellHarness.RunCmdScriptAsync(
            FailureScript(new CmdEmitter(), 11), scriptDir.Path, cwd.Path, TestContext.Current.CancellationToken);

        Assert.Contains("FAILED_WITH_11", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bash_ReadsBackEveryValueExactlyAndExecutesNothing()
    {
        var bash = ShellHarness.FindBash();
        Assert.SkipUnless(bash is not null, "bash is not installed on this machine.");
        var (script, names) = Build(new BashEmitter(), Hostile);
        using var cwd = new TempDirectory();

        var actual = await ShellHarness.RunBashAsync(bash, script, names, cwd.Path, TestContext.Current.CancellationToken);

        AssertRoundTrip(Hostile, actual, cwd.Path);
    }

    [Fact]
    public async Task PowerShell7_ReadsBackEveryValueExactlyAndExecutesNothing()
    {
        var pwsh = ShellHarness.FindPwsh();
        Assert.SkipUnless(pwsh is not null, "PowerShell 7 (pwsh) is not installed on this machine.");
        var (script, names) = Build(new PowerShellEmitter(), Hostile);
        using var cwd = new TempDirectory();

        var actual = await ShellHarness.RunPowerShellAsync(pwsh, script, names, cwd.Path, TestContext.Current.CancellationToken);

        AssertRoundTrip(Hostile, actual, cwd.Path);
    }

    [Fact]
    public async Task WindowsPowerShell51_ReadsBackEveryValueExactlyAndExecutesNothing()
    {
        var powershell = ShellHarness.FindWindowsPowerShell();
        Assert.SkipUnless(powershell is not null, "Windows PowerShell 5.1 exists only on Windows.");
        var (script, names) = Build(new PowerShellEmitter(), Hostile);
        using var cwd = new TempDirectory();

        var actual = await ShellHarness.RunPowerShellAsync(powershell, script, names, cwd.Path, TestContext.Current.CancellationToken);

        AssertRoundTrip(Hostile, actual, cwd.Path);
    }

    [Fact]
    public async Task Cmd_ReadsBackEveryAcceptedValueExactlyAndExecutesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe exists only on Windows.");
        var (script, _) = Build(new CmdEmitter(), CmdSafe);
        using var scriptDir = new TempDirectory();
        using var cwd = new TempDirectory();

        var actual = await ShellHarness.RunCmdAsync(script, Prefix, CmdSafe.Length, scriptDir.Path, cwd.Path, TestContext.Current.CancellationToken);

        AssertRoundTrip(CmdSafe, actual, cwd.Path);
    }
}
