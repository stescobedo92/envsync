using System.Text.Json;
using EnvSync.Application;
using EnvSync.Domain;

namespace EnvSync.Cli.Tests;

public sealed class CheckCommandTests
{
    [Fact]
    public async Task Check_WhenEverythingIsAvailable_ExitsZeroAndListsEveryVariable()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("check");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("DB_PASSWORD", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("API_KEY", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("OK", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("SKIPPED", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_NeverPrintsASecretValue()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("check");

        Assert.DoesNotContain(CliHarness.DbSecret, result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(CliHarness.ApiSecret, result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ReportsWhichRequiredKeyIsMissing()
    {
        using var cli = new CliHarness();
        cli.Provider.Fails("api-key", ErrorKind.SecretNotFound, "no such secret");

        var result = await cli.RunAsync("check");

        Assert.Equal(ExitCodes.MissingRequired, result.ExitCode);
        var missing = result.Stdout.Split('\n').Single(line => line.Contains("API_KEY", StringComparison.Ordinal));
        Assert.Contains("MISSING", missing, StringComparison.Ordinal);
        Assert.Contains("kv", missing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_AProviderFailureOutranksAMissingKey()
    {
        using var cli = new CliHarness();
        cli.Provider.Fails("api-key", ErrorKind.SecretNotFound).Fails("db-password", ErrorKind.AuthenticationFailed, "expired");

        var result = await cli.RunAsync("check");

        Assert.Equal(ExitCodes.ProviderFailure, result.ExitCode);
    }

    [Fact]
    public async Task Check_Offline_NeverAsksTheProviderForAnything()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("check", "--offline");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, cli.Provider.CallCount);
        Assert.Contains("UNVERIFIED", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_Json_IsMachineReadableAndHasNoValues()
    {
        using var cli = new CliHarness();
        cli.Provider.Fails("api-key", ErrorKind.SecretNotFound, "no such secret");

        var result = await cli.RunAsync("check", "--format", "json");

        Assert.Equal(ExitCodes.MissingRequired, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.Equal("dev", root.GetProperty("profile").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(ExitCodes.MissingRequired, root.GetProperty("exitCode").GetInt32());

        var variables = root.GetProperty("variables").EnumerateArray().ToDictionary(v => v.GetProperty("name").GetString()!);
        Assert.Equal("resolved", variables["DB_PASSWORD"].GetProperty("status").GetString());
        Assert.Equal("kv", variables["DB_PASSWORD"].GetProperty("provider").GetString());
        Assert.True(variables["DB_PASSWORD"].GetProperty("required").GetBoolean());
        Assert.Equal("missing", variables["API_KEY"].GetProperty("status").GetString());
        Assert.Equal("SecretNotFound", variables["API_KEY"].GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("skipped", variables["LOG_LEVEL"].GetProperty("status").GetString());
        Assert.False(variables["LOG_LEVEL"].GetProperty("required").GetBoolean());
        Assert.DoesNotContain(CliHarness.DbSecret, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_Json_WhenAllIsWell_SaysOkAndExitsZero()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("check", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Check_UnknownFormat_IsAUsageError()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("check", "--format", "xml");

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
    }

    [Fact]
    public async Task Check_ShowsTheReasonForAFailure()
    {
        using var cli = new CliHarness();
        cli.Provider.Fails("db-password", ErrorKind.AuthenticationFailed, "token expired");

        var result = await cli.RunAsync("check");

        Assert.Contains("AUTH", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("token expired", result.Stdout, StringComparison.Ordinal);
    }
}
