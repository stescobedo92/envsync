using EnvSync.Application;
using EnvSync.Domain;

namespace EnvSync.Cli.Tests;

public sealed class RunCommandTests
{
    [Fact]
    public async Task Run_InjectsTheSecretsIntoTheChildAndReturnsItsExitCode()
    {
        using var cli = new CliHarness();
        cli.Launcher.Response = Result<int>.Success(7);

        var result = await cli.RunAsync("run", "--", "my-app", "--flag", "two words");

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("my-app", cli.Launcher.LastRequest.Executable);
        Assert.Equal(["--flag", "two words"], cli.Launcher.LastRequest.Arguments);
        var environment = cli.Launcher.LastRequest.Environment.ToDictionary(e => e.Name.Value, e => e.Value.Reveal());
        Assert.Equal(CliHarness.DbSecret, environment["DB_PASSWORD"]);
        Assert.Equal(CliHarness.ApiSecret, environment["API_KEY"]);
        Assert.DoesNotContain("LOG_LEVEL", environment.Keys);
    }

    [Fact]
    public async Task Run_NeverPrintsASecretAndLeavesStdoutToTheChild()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("run", "--", "my-app");

        Assert.Empty(result.StdoutBytes);
        Assert.DoesNotContain(CliHarness.DbSecret, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(CliHarness.ApiSecret, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WarnsAboutAnOptionalVariableThatWasLeftUnset()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("run", "--", "my-app");

        Assert.Contains("warning", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOG_LEVEL", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenARequiredKeyIsMissing_DoesNotLaunchAndExplainsWhatIsMissing()
    {
        using var cli = new CliHarness();
        cli.Provider.Fails("api-key", ErrorKind.SecretNotFound, "no such secret");

        var result = await cli.RunAsync("run", "--", "my-app");

        Assert.Equal(ExitCodes.MissingRequired, result.ExitCode);
        Assert.Equal(0, cli.Launcher.Calls);
        Assert.Contains("API_KEY", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("MISSING", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("kv", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(CliHarness.DbSecret, result.Stderr, StringComparison.Ordinal);
        Assert.Empty(result.StdoutBytes);
    }

    [Fact]
    public async Task Run_WhenAProviderRejectsTheCredentials_ExitsWithTheProviderCode()
    {
        using var cli = new CliHarness();
        cli.Provider.Fails("db-password", ErrorKind.AuthenticationFailed, "token expired");

        var result = await cli.RunAsync("run", "--", "my-app");

        Assert.Equal(ExitCodes.ProviderFailure, result.ExitCode);
        Assert.Equal(0, cli.Launcher.Calls);
        Assert.Contains("AUTH", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("token expired", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenTheExecutableIsNotFound_ExitsWith127AndSaysSo()
    {
        using var cli = new CliHarness();
        cli.Launcher.Response = Result<int>.Failure(new Error(ErrorKind.ExecutableNotFound, "nope", "'nope' was not found on PATH."));

        var result = await cli.RunAsync("run", "--", "nope");

        Assert.Equal(ExitCodes.ExecutableNotFound, result.ExitCode);
        Assert.Contains("not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_WithoutACommand_IsAUsageError()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("run");

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Equal(0, cli.Launcher.Calls);
        Assert.Equal(0, cli.Provider.CallCount);
    }

    [Fact]
    public async Task Run_EverythingAfterTheDoubleDashBelongsToTheChild()
    {
        using var cli = new CliHarness();

        await cli.RunAsync("run", "--profile", "dev", "--", "my-app", "--version", "--profile", "other", "-v");

        Assert.Equal(["--version", "--profile", "other", "-v"], cli.Launcher.LastRequest.Arguments);
    }

    [Fact]
    public async Task Run_OptionsBeforeTheDoubleDash_SelectTheProfile()
    {
        using var cli = new CliHarness();
        cli.Provider.Returns("db-password-staging", "STAGING-secret");

        await cli.RunAsync("run", "--profile", "staging", "--", "my-app");

        var assignment = Assert.Single(cli.Launcher.LastRequest.Environment);
        Assert.Equal("DB_PASSWORD", assignment.Name.Value);
        Assert.Equal("STAGING-secret", assignment.Value.Reveal());
    }

    [Fact]
    public async Task Run_Verbose_ReportsTheProfileWithoutEverShowingValues()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("run", "--verbose", "--", "my-app");

        Assert.Contains("dev", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(CliHarness.DbSecret, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_TimeoutAppliesToEachSecret()
    {
        using var cli = new CliHarness();
        cli.Provider.Hangs("api-key");

        var result = await cli.RunAsync("run", "--timeout", "1", "--", "my-app");

        Assert.Equal(ExitCodes.ProviderFailure, result.ExitCode);
        Assert.Contains("TIMEOUT", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, cli.Launcher.Calls);
    }
}
