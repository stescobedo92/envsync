using EnvSync.Application.Providers;
using EnvSync.Application.Requirements;
using EnvSync.Application.Resolution;
using EnvSync.Domain;
using EnvSync.TestSupport;
using static EnvSync.TestSupport.TestProfiles;

namespace EnvSync.Application.Tests;

public sealed class CheckRequirementsQueryHandlerTests
{
    private static readonly ResolveOptions Sane = new(4, TimeSpan.FromSeconds(10));

    private static CheckRequirementsQueryHandler HandlerFor(FakeSecretProviderFactory factory) =>
        new(new ResolveEnvironmentQueryHandler(new ProviderRegistry([factory])));

    [Fact]
    public async Task Check_ReportsWhichRequiredKeysAreMissing()
    {
        using var kv = new FakeSecretProvider().Returns("present", "x");
        var profile = Create(
            [Provider("kv")],
            [Variable("PRESENT", "kv", "present"), Variable("ABSENT", "kv", "absent")]);

        var result = await HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv))
            .HandleAsync(new CheckRequirementsQuery(profile, Sane, Offline: false), TestContext.Current.CancellationToken);

        Assert.False(result.IsSatisfied);
        Assert.Equal(VariableStatus.Resolved, result.Outcomes[0].Status);
        Assert.Equal(VariableStatus.Missing, result.Outcomes[1].Status);
    }

    [Fact]
    public async Task Check_NeverKeepsTheSecretValues()
    {
        using var kv = new FakeSecretProvider().Returns("db-password", "p1");
        var profile = Create([Provider("kv")], [Variable("DB_PASSWORD", "kv", "db-password")]);

        var result = await HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv))
            .HandleAsync(new CheckRequirementsQuery(profile, Sane, Offline: false), TestContext.Current.CancellationToken);

        Assert.All(result.Outcomes, outcome => Assert.Equal(string.Empty, outcome.Value.Reveal()));
    }

    [Fact]
    public async Task Check_Offline_DoesNotTouchTheNetwork()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv))
            .HandleAsync(new CheckRequirementsQuery(profile, Sane, Offline: true), TestContext.Current.CancellationToken);

        Assert.Equal(0, kv.CallCount);
        Assert.Equal(VariableStatus.Unverified, Assert.Single(result.Outcomes).Status);
    }
}
