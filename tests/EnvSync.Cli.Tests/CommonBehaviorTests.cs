using EnvSync.Application;
using EnvSync.Domain;

namespace EnvSync.Cli.Tests;

public sealed class CommonBehaviorTests
{
    [Fact]
    public async Task Help_ListsTheThreeCommands()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("run", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("env", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("check", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("env")]
    [InlineData("check")]
    public async Task EachCommandHasHelp(string command)
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync(command, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--manifest", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("--profile", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_PrintsTheVersion()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"\d+\.\d+\.\d+", result.Stdout);
    }

    [Theory]
    [InlineData]
    [InlineData("frobnicate")]
    [InlineData("--nonsense")]
    public async Task UnknownInput_IsAUsageErrorThatPointsToHelp(params string[] args)
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync(args);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("--help", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, cli.Provider.CallCount);
    }

    [Theory]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "-3")]
    [InlineData("--timeout", "abc")]
    [InlineData("--concurrency", "0")]
    [InlineData("--concurrency", "-1")]
    [InlineData("--concurrency", "many")]
    public async Task InvalidNumericOptions_AreUsageErrors(string option, string value)
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("check", option, value);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains(option, result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, cli.Provider.CallCount);
    }

    [Fact]
    public async Task ConcurrencyOption_IsHonoured()
    {
        using var cli = new CliHarness(BigManifest());
        for (var i = 0; i < 6; i++)
        {
            cli.Provider.Returns($"s{i}", $"v{i}");
        }

        var result = await cli.RunAsync("check", "--concurrency", "1");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, cli.Provider.MaxConcurrency);
    }

    [Fact]
    public async Task CancelledByTheUser_ExitsWith130AndNeverLaunches()
    {
        using var cli = new CliHarness();
        cli.Provider.Hangs("api-key");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var result = await cli.RunAsync(cts.Token, "run", "--timeout", "30", "--", "my-app");

        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Contains("cancel", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, cli.Launcher.Calls);
    }

    [Fact]
    public async Task AProviderBug_IsContainedPerVariableAndExitsWith13NotAsAnOutage()
    {
        using var cli = new CliHarness();
        cli.Launcher.Response = Result<int>.Success(0);
        cli.Provider.Throws("api-key", new InvalidOperationException("boom"));

        var result = await cli.RunAsync("check");

        Assert.Equal(ExitCodes.InternalError, result.ExitCode);
        Assert.Contains("InvalidOperationException", result.Stderr + result.Stdout, StringComparison.Ordinal);
        Assert.Contains("boom", result.Stderr + result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(CliHarness.DbSecret, result.Stderr + result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANetworkStyleProviderFailure_ExitsWith12BecauseARetryMayHelp()
    {
        using var cli = new CliHarness();
        cli.Provider.Throws("api-key", new IOException("connection reset"));

        var result = await cli.RunAsync("check");

        Assert.Equal(ExitCodes.ProviderFailure, result.ExitCode);
    }

    [Fact]
    public async Task ACrashOutsideTheProviders_IsAnInternalErrorNamingTheProblem()
    {
        using var cli = new CliHarness();
        cli.Launcher.Response = Result<int>.Success(0);
        var crashing = new CrashingLauncher();

        var result = await cli.RunWithLauncherAsync(crashing, "run", "--", "my-app");

        Assert.Equal(ExitCodes.InternalError, result.ExitCode);
        Assert.Contains("unexpected", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kaboom", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(CliHarness.DbSecret, result.Stderr, StringComparison.Ordinal);
    }

    private static string BigManifest()
    {
        const string template = """{"profiles": {"dev": {"providers": {"kv": {"type": "fake"}}, "variables": { __VARIABLES__ }}}}""";
        var variables = string.Join(",", Enumerable.Range(0, 6).Select(i => $"\"VAR_{i}\": {{ \"from\": \"kv\", \"ref\": \"s{i}\" }}"));
        return template.Replace("__VARIABLES__", variables, StringComparison.Ordinal);
    }
}
