using EnvSync.Application;

namespace EnvSync.Cli.Tests;

public sealed class ManifestAndProfileTests
{
    [Fact]
    public async Task NoManifestAnywhere_ExplainsHowToProvideOne()
    {
        using var cli = new CliHarness(manifest: null);

        var result = await cli.RunAsync("check");

        if (result.ExitCode == 0)
        {
            Assert.Skip("An envsync.json exists above the temp directory on this machine.");
        }

        Assert.Equal(ExitCodes.ManifestInvalid, result.ExitCode);
        Assert.Contains("envsync.json", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("--manifest", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitManifest_IsUsedInsteadOfTheDiscoveredOne()
    {
        using var cli = new CliHarness();
        cli.Write("other/custom.json", """{"profiles": {"only": {"providers": {"kv": {"type": "fake"}}, "variables": {"ONLY_VAR": {"from": "kv", "ref": "db-password"}}}}}""");

        var result = await cli.RunAsync("env", "--shell", "bash", "--manifest", "other/custom.json");

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("export ONLY_VAR=", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidManifest_ReportsEveryProblemWithItsJsonPath()
    {
        using var cli = new CliHarness("""{"profiles": {"dev": {"providers": {}, "variables": {"1BAD": {"from": "kv", "ref": "a"}, "OK": {"from": "ghost", "ref": "b"}}}}}""");

        var result = await cli.RunAsync("check");

        Assert.Equal(ExitCodes.ManifestInvalid, result.ExitCode);
        Assert.Contains("profiles.dev.variables.1BAD", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("ghost", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, cli.Provider.CallCount);
    }

    [Fact]
    public async Task UnsupportedProviderType_IsCaughtBeforeAnyNetworkCall()
    {
        using var cli = new CliHarness("""{"profiles": {"dev": {"providers": {"x": {"type": "oracle-wallet"}}, "variables": {"A": {"from": "x", "ref": "a"}}}}}""");

        var result = await cli.RunAsync("check");

        Assert.Equal(ExitCodes.ManifestInvalid, result.ExitCode);
        Assert.Contains("oracle-wallet", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("fake", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownProfile_ListsTheAvailableOnes()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("check", "--profile", "prod");

        Assert.Equal(ExitCodes.ManifestInvalid, result.ExitCode);
        Assert.Contains("prod", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("dev", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("staging", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralProfilesAndNoDefault_AsksTheUserToChoose()
    {
        using var cli = new CliHarness(CliHarness.Manifest(defaultProfile: null));

        var result = await cli.RunAsync("check");

        Assert.Equal(ExitCodes.ManifestInvalid, result.ExitCode);
        Assert.Contains("--profile", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultProfileOfTheManifest_IsUsedWhenNoneIsGiven()
    {
        using var cli = new CliHarness(CliHarness.Manifest(defaultProfile: "staging"));
        cli.Provider.Returns("db-password-staging", "STAGING");

        var result = await cli.RunAsync("env", "--shell", "bash");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("export DB_PASSWORD='STAGING'\n", result.Stdout);
    }

    // An empty value is not "unset": CI often expands STAGE="" into ENVSYNC_PROFILE, and silently falling back to a manifest whose
    // defaultProfile is production would run the wrong environment.
    [Theory]
    [InlineData("--profile", "")]
    [InlineData("--profile", "  ")]
    [InlineData("--manifest", "")]
    [InlineData("--manifest", " ")]
    public async Task ABlankProfileOrManifestOption_IsAUsageErrorNotASilentFallback(string option, string value)
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("check", option, value);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains(option, result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, cli.Provider.CallCount);
    }

    [Fact]
    public async Task ABlankEnvsyncProfileVariable_IsAUsageErrorNotASilentFallbackToTheDefault()
    {
        using var cli = new CliHarness();
        cli.Environment["ENVSYNC_PROFILE"] = string.Empty;

        var result = await cli.RunAsync("check");

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("ENVSYNC_PROFILE", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, cli.Provider.CallCount);
    }

    [Fact]
    public async Task AProfileChosenByDefault_IsAnnouncedSoADefaultCannotBeMistakenForAChoice()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("check");

        Assert.Contains("using profile 'dev'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("defaultProfile", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitlyChosenProfile_IsNotAnnouncedBecauseTheUserJustSaidIt()
    {
        using var cli = new CliHarness();

        var result = await cli.RunAsync("check", "--profile", "dev");

        Assert.DoesNotContain("using profile", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEnvsyncProfileVariable_SelectsTheProfile()
    {
        using var cli = new CliHarness();
        cli.Environment["ENVSYNC_PROFILE"] = "staging";
        cli.Provider.Returns("db-password-staging", "STAGING");

        var result = await cli.RunAsync("env", "--shell", "bash");

        Assert.Equal("export DB_PASSWORD='STAGING'\n", result.Stdout);
    }

    [Fact]
    public async Task TheProfileOption_BeatsTheEnvironmentVariable()
    {
        using var cli = new CliHarness();
        cli.Environment["ENVSYNC_PROFILE"] = "staging";

        var result = await cli.RunAsync("env", "--shell", "bash", "--profile", "dev");

        Assert.Contains("API_KEY", result.Stdout, StringComparison.Ordinal);
    }
}
