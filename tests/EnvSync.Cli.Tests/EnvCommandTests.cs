using EnvSync.Application;
using EnvSync.Domain;

namespace EnvSync.Cli.Tests;

public sealed class EnvCommandTests
{
    [Fact]
    public async Task Env_Bash_WritesAnExportPerResolvedVariableToStdout()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("env", "--shell", "bash");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"export DB_PASSWORD='{CliHarness.DbSecret}'\nexport API_KEY='{CliHarness.ApiSecret}'\n", result.Stdout);
    }

    [Fact]
    public async Task Env_PowerShell_WritesEnvAssignments()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("env", "--shell", "pwsh");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"$env:DB_PASSWORD = '{CliHarness.DbSecret}'\n$env:API_KEY = '{CliHarness.ApiSecret}'\n", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("PWSH")]
    public async Task Env_PowerShellAliases_AreAccepted(string shell)
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("env", "--shell", shell);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("$env:", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_Zsh_UsesTheSameSyntaxAsBash()
    {
        using var cli = new CliHarness();

        var bash = await cli.RunAsync("env", "--shell", "bash");
        var zsh = await cli.RunAsync("env", "--shell", "zsh");

        Assert.Equal(bash.Stdout, zsh.Stdout);
    }

    [Fact]
    public async Task Env_Cmd_WritesSetStatementsWithCrLf()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("env", "--shell", "cmd");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("set \"DB_PASSWORD=" + CliHarness.DbSecret + "\"\r\nset \"API_KEY=" + CliHarness.ApiSecret + "\"\r\n", result.Stdout);
    }

    [Fact]
    public async Task Env_WithoutAShell_UsesTheNativeOneForThisOperatingSystem()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("env");

        var expectedStart = OperatingSystem.IsWindows() ? "$env:" : "export ";
        Assert.StartsWith(expectedStart, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_KeepsStdoutForTheScriptAndPutsEverythingElseOnStderr()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("env", "--shell", "bash", "--verbose");

        Assert.All(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith("export ", line, StringComparison.Ordinal));
        Assert.DoesNotContain(CliHarness.DbSecret, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(CliHarness.ApiSecret, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_WritesUtf8WithoutAByteOrderMark()
    {
        using var cli = new CliHarness();
        cli.Provider.Returns("db-password", "café-日本");

        var result = await cli.RunAsync("env", "--shell", "bash");

        Assert.NotEqual(0xEF, result.StdoutBytes[0]);
        Assert.Contains("café-日本", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_RefusesToPrintSecretsToATerminalAndDoesNotEvenFetchThem()
    {
        using var cli = new CliHarness { StdoutIsRedirected = false };

        var result = await cli.RunAsync("env", "--shell", "bash");

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.Contains("--unsafe-print", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, cli.Provider.CallCount);
    }

    [Fact]
    public async Task Env_UnsafePrint_AllowsPrintingToATerminal()
    {
        using var cli = new CliHarness { StdoutIsRedirected = false };

        var result = await cli.RunAsync("env", "--shell", "bash", "--unsafe-print");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("export DB_PASSWORD=", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_WhenARequiredKeyIsMissing_WritesNothingToStdoutAndReports()
    {
        using var cli = new CliHarness();
        cli.Provider.Fails("api-key", ErrorKind.SecretNotFound, "no such secret");

        var result = await cli.RunAsync("env", "--shell", "bash");

        Assert.Equal(ExitCodes.MissingRequired, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.Contains("API_KEY", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("MISSING", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_WhenTheShellCannotCarryAValue_ExplainsAndSuggestsRun()
    {
        using var cli = new CliHarness();
        cli.Provider.Returns("db-password", "100%-sure");

        var result = await cli.RunAsync("env", "--shell", "cmd");

        Assert.Equal(ExitCodes.ManifestInvalid, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.Contains("DB_PASSWORD", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("envsync run", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("100%-sure", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_UnknownShell_IsAUsageError()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("env", "--shell", "fish");

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.Equal(0, cli.Provider.CallCount);
    }
}
