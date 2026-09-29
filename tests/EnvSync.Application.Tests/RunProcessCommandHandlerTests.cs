using System.Collections.Immutable;
using EnvSync.Application.Providers;
using EnvSync.Application.Resolution;
using EnvSync.Application.Run;
using EnvSync.Domain;
using EnvSync.TestSupport;
using static EnvSync.TestSupport.TestProfiles;

namespace EnvSync.Application.Tests;

public sealed class RunProcessCommandHandlerTests
{
    private static readonly ResolveOptions Sane = new(4, TimeSpan.FromSeconds(10));
    private static readonly ImmutableArray<string> Args = ["run", "--no-build"];

    private static RunProcessCommandHandler HandlerFor(FakeSecretProvider provider, FakeProcessLauncher launcher) =>
        new(
            new ResolveEnvironmentQueryHandler(new ProviderRegistry([FakeSecretProviderFactory.Returning(FakeType, provider)])),
            launcher);

    [Fact]
    public async Task Run_WhenEverythingResolves_LaunchesTheProcessWithTheSecretsInItsEnvironment()
    {
        using var kv = new FakeSecretProvider().Returns("db-password", "p1").Returns("api", "k1");
        var launcher = new FakeProcessLauncher { Response = Result<int>.Success(7) };
        var profile = Create([Provider("kv")], [Variable("DB_PASSWORD", "kv", "db-password"), Variable("API_KEY", "kv", "api")]);

        var result = await HandlerFor(kv, launcher)
            .HandleAsync(new RunProcessCommand(profile, Sane, "dotnet", Args), TestContext.Current.CancellationToken);

        Assert.True(result.Launched);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(1, launcher.Calls);
        Assert.Equal("dotnet", launcher.LastRequest.Executable);
        Assert.Equal(Args, launcher.LastRequest.Arguments);
        Assert.Collection(
            launcher.LastRequest.Environment,
            first =>
            {
                Assert.Equal("DB_PASSWORD", first.Name.Value);
                Assert.Equal("p1", first.Value.Reveal());
            },
            second =>
            {
                Assert.Equal("API_KEY", second.Name.Value);
                Assert.Equal("k1", second.Value.Reveal());
            });
    }

    [Fact]
    public async Task Run_SkippedOptionalVariables_AreNotPassedToTheProcess()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var launcher = new FakeProcessLauncher();
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a"), Variable("OPT", "kv", "nope", required: false)]);

        await HandlerFor(kv, launcher).HandleAsync(new RunProcessCommand(profile, Sane, "app", []), TestContext.Current.CancellationToken);

        Assert.Equal("A", Assert.Single(launcher.LastRequest.Environment).Name.Value);
    }

    [Fact]
    public async Task Run_WhenARequiredKeyIsMissing_DoesNotLaunchAndExplainsWhy()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var launcher = new FakeProcessLauncher();
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a"), Variable("B", "kv", "missing")]);

        var result = await HandlerFor(kv, launcher)
            .HandleAsync(new RunProcessCommand(profile, Sane, "app", Args), TestContext.Current.CancellationToken);

        Assert.False(result.Launched);
        Assert.Equal(ExitCodes.MissingRequired, result.ExitCode);
        Assert.Equal(0, launcher.Calls);
        Assert.False(result.Resolution.IsSatisfied);
    }

    [Fact]
    public async Task Run_WhenAProviderRejectsTheCredentials_ExitsWithTheProviderCode()
    {
        using var kv = new FakeSecretProvider().Fails("a", ErrorKind.AuthenticationFailed, "expired");
        var launcher = new FakeProcessLauncher();
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await HandlerFor(kv, launcher)
            .HandleAsync(new RunProcessCommand(profile, Sane, "app", Args), TestContext.Current.CancellationToken);

        Assert.False(result.Launched);
        Assert.Equal(ExitCodes.ProviderFailure, result.ExitCode);
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public async Task Run_WhenTheExecutableCannotBeFound_ReportsItWithTheConventionalCode()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var launcher = new FakeProcessLauncher
        {
            Response = Result<int>.Failure(new Error(ErrorKind.ExecutableNotFound, "nope", "not on PATH")),
        };
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await HandlerFor(kv, launcher)
            .HandleAsync(new RunProcessCommand(profile, Sane, "nope", Args), TestContext.Current.CancellationToken);

        Assert.False(result.Launched);
        Assert.Equal(ExitCodes.ExecutableNotFound, result.ExitCode);
        Assert.Equal(ErrorKind.ExecutableNotFound, result.LaunchError?.Kind);
    }

    [Fact]
    public async Task Run_TellsTheCallerWhatWasResolvedBeforeTheChildStarts()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var launcher = new FakeProcessLauncher();
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a"), Variable("OPT", "kv", "nope", required: false)]);
        ResolutionResult? seen = null;
        var launchesWhenTold = -1;
        var command = new RunProcessCommand(profile, Sane, "app", Args, Resolved: resolution =>
        {
            seen = resolution;
            launchesWhenTold = launcher.Calls;
        });

        await HandlerFor(kv, launcher).HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.Contains(seen.Outcomes, o => o.Status == VariableStatus.Skipped);
        Assert.Equal(0, launchesWhenTold);
        Assert.Equal(1, launcher.Calls);
    }

    [Fact]
    public async Task Run_DoesNotTellTheCallerWhenPreflightFails()
    {
        using var kv = new FakeSecretProvider();
        var launcher = new FakeProcessLauncher();
        var profile = Create([Provider("kv")], [Variable("A", "kv", "missing")]);
        var told = false;
        var command = new RunProcessCommand(profile, Sane, "app", Args, Resolved: _ => told = true);

        var result = await HandlerFor(kv, launcher).HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.False(told);
        Assert.False(result.Launched);
    }

    [Fact]
    public async Task Run_ChildExitCodes_PassThroughUntouched()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var launcher = new FakeProcessLauncher { Response = Result<int>.Success(ExitCodes.MissingRequired) };
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await HandlerFor(kv, launcher)
            .HandleAsync(new RunProcessCommand(profile, Sane, "app", Args), TestContext.Current.CancellationToken);

        Assert.True(result.Launched);
        Assert.Equal(ExitCodes.MissingRequired, result.ExitCode);
    }
}
