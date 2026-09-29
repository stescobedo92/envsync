using EnvSync.Application.Abstractions;
using EnvSync.Application.Providers;
using EnvSync.Application.Resolution;
using EnvSync.Domain;
using EnvSync.TestSupport;
using static EnvSync.TestSupport.TestProfiles;

namespace EnvSync.Application.Tests;

public sealed class ResolveEnvironmentQueryHandlerTests
{
    private static readonly ResolveOptions Sane = new(MaxConcurrency: 4, Timeout: TimeSpan.FromSeconds(10));

    private static ResolveEnvironmentQueryHandler HandlerFor(params FakeSecretProviderFactory[] factories) =>
        new(new ProviderRegistry(factories));

    private static ValueTask<ResolutionResult> Resolve(
        ResolveEnvironmentQueryHandler handler,
        Profile profile,
        ResolveMode mode = ResolveMode.Retain,
        ResolveOptions? options = null,
        CancellationToken cancellationToken = default) =>
        handler.HandleAsync(new ResolveEnvironmentQuery(profile, options ?? Sane, mode), cancellationToken);

    [Fact]
    public async Task Resolve_AllVariablesAvailable_ReturnsThemInManifestOrder()
    {
        using var kv = new FakeSecretProvider().Returns("db-password", "p1").Returns("api", "k1");
        var profile = Create(
            [Provider("kv")],
            [Variable("DB_PASSWORD", "kv", "db-password"), Variable("API_KEY", "kv", "api")]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSatisfied);
        Assert.Collection(
            result.Outcomes,
            first =>
            {
                Assert.Equal("DB_PASSWORD", first.Spec.Name.Value);
                Assert.Equal(VariableStatus.Resolved, first.Status);
                Assert.Equal("p1", first.Value.Reveal());
            },
            second =>
            {
                Assert.Equal("API_KEY", second.Spec.Name.Value);
                Assert.Equal("k1", second.Value.Reveal());
            });
    }

    [Fact]
    public async Task Resolve_RequiredSecretNotFound_IsMissingAndNotSatisfied()
    {
        using var kv = new FakeSecretProvider().Fails("db-password", ErrorKind.SecretNotFound, "no such secret");
        var profile = Create([Provider("kv")], [Variable("DB_PASSWORD", "kv", "db-password")]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSatisfied);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(VariableStatus.Missing, outcome.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.SecretNotFound, error.Kind);
        Assert.Equal("DB_PASSWORD", error.Subject);
    }

    [Fact]
    public async Task Resolve_OptionalSecretNotFound_IsSkippedAndStillSatisfied()
    {
        using var kv = new FakeSecretProvider().Fails("log-level", ErrorKind.SecretNotFound);
        var profile = Create([Provider("kv")], [Variable("LOG_LEVEL", "kv", "log-level", required: false)]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSatisfied);
        Assert.Equal(VariableStatus.Skipped, Assert.Single(result.Outcomes).Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Resolve_OptionalSecretWhoseProviderRejectsTheCredentials_StillFails()
    {
        using var kv = new FakeSecretProvider().Fails("log-level", ErrorKind.AuthenticationFailed, "expired token");
        var profile = Create([Provider("kv")], [Variable("LOG_LEVEL", "kv", "log-level", required: false)]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSatisfied);
        Assert.Equal(VariableStatus.Failed, Assert.Single(result.Outcomes).Status);
    }

    [Fact]
    public async Task Resolve_RequiredFieldNotFound_IsMissing()
    {
        using var kv = new FakeSecretProvider().Fails("db#password", ErrorKind.FieldNotFound, "no field");
        var profile = Create([Provider("kv")], [Variable("DB_PASSWORD", "kv", "db#password")]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(VariableStatus.Missing, Assert.Single(result.Outcomes).Status);
    }

    [Fact]
    public async Task Resolve_ConfigurationErrorFromTheProvider_IsAFailureNotAMissingKey()
    {
        using var vault = new FakeSecretProvider().Fails("app", ErrorKind.FieldRequired, "needs #field");
        var profile = Create([Provider("vault")], [Variable("JWT", "vault", "app")]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, vault)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(VariableStatus.Failed, Assert.Single(result.Outcomes).Status);
    }

    [Fact]
    public async Task Resolve_ProviderCannotBeCreated_FailsOnlyTheVariablesThatUseIt()
    {
        using var kv = new FakeSecretProvider().Returns("db-password", "p1");
        var kvFactory = FakeSecretProviderFactory.Returning("fake-kv", kv);
        var awsFactory = FakeSecretProviderFactory.Failing("fake-aws", ErrorKind.AuthenticationFailed, "no credentials");
        var profile = Create(
            [Provider("kv", "fake-kv"), Provider("aws", "fake-aws")],
            [Variable("DB_PASSWORD", "kv", "db-password"), Variable("API_KEY", "aws", "api")]);

        var result = await Resolve(HandlerFor(kvFactory, awsFactory), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(VariableStatus.Resolved, result.Outcomes[0].Status);
        var failed = result.Outcomes[1];
        Assert.Equal(VariableStatus.Failed, failed.Status);
        Assert.Equal(ErrorKind.AuthenticationFailed, failed.Error.Kind);
        Assert.Equal("API_KEY", failed.Error.Subject);
        Assert.Contains("aws", failed.Error.Detail, StringComparison.Ordinal);
        Assert.Contains("no credentials", failed.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_VariableUsingAnUndeclaredAlias_FailsAsUnknownProvider()
    {
        var profile = Create([], [Variable("API_KEY", "ghost", "api")]);

        var result = await Resolve(HandlerFor(), profile, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(VariableStatus.Failed, outcome.Status);
        Assert.Equal(ErrorKind.ProviderUnknown, outcome.Error.Kind);
    }

    [Fact]
    public async Task Resolve_ManyVariablesOfOneAlias_BuildsThatProviderOnce()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1").Returns("b", "2").Returns("c", "3");
        var factory = FakeSecretProviderFactory.Returning(FakeType, kv);
        var profile = Create(
            [Provider("kv")],
            [Variable("A", "kv", "a"), Variable("B", "kv", "b"), Variable("C", "kv", "c")]);

        await Resolve(HandlerFor(factory), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(3, kv.CallCount);
    }

    [Fact]
    public async Task Resolve_ProviderNoVariableUses_IsNeverBuilt()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var used = FakeSecretProviderFactory.Returning("fake-kv", kv);
        var unused = FakeSecretProviderFactory.Failing("fake-aws", ErrorKind.ProviderMisconfigured);
        var profile = Create(
            [Provider("kv", "fake-kv"), Provider("aws", "fake-aws")],
            [Variable("A", "kv", "a")]);

        var result = await Resolve(HandlerFor(used, unused), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSatisfied);
        Assert.Equal(0, unused.CreateCalls);
    }

    [Fact]
    public async Task Resolve_RespectsTheConcurrencyLimit()
    {
        using var kv = new FakeSecretProvider { Delay = TimeSpan.FromMilliseconds(60) };
        var variables = new List<VariableSpec>();
        for (var i = 0; i < 12; i++)
        {
            kv.Returns($"s{i}", $"v{i}");
            variables.Add(Variable($"VAR_{i}", "kv", $"s{i}"));
        }

        var profile = Create([Provider("kv")], variables);

        await Resolve(
            HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)),
            profile,
            options: new ResolveOptions(3, TimeSpan.FromSeconds(10)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.InRange(kv.MaxConcurrency, 2, 3);
    }

    [Fact]
    public async Task Resolve_NonPositiveConcurrency_FallsBackToOneAtATime()
    {
        using var kv = new FakeSecretProvider { Delay = TimeSpan.FromMilliseconds(10) }.Returns("a", "1").Returns("b", "2");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a"), Variable("B", "kv", "b")]);

        var result = await Resolve(
            HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)),
            profile,
            options: new ResolveOptions(0, TimeSpan.FromSeconds(10)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSatisfied);
        Assert.Equal(1, kv.MaxConcurrency);
    }

    [Fact]
    public async Task Resolve_SlowVariablesStillComeBackInManifestOrder()
    {
        using var kv = new FakeSecretProvider { Delay = TimeSpan.FromMilliseconds(15) };
        var variables = new List<VariableSpec>();
        for (var i = 0; i < 10; i++)
        {
            kv.Returns($"s{i}", $"v{i}");
            variables.Add(Variable($"VAR_{i}", "kv", $"s{i}"));
        }

        var result = await Resolve(
            HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)),
            Create([Provider("kv")], variables),
            options: new ResolveOptions(10, TimeSpan.FromSeconds(10)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            variables.Select(v => v.Name.Value),
            result.Outcomes.Select(o => o.Spec.Name.Value));
    }

    [Fact]
    public async Task Resolve_VariableThatExceedsTheTimeout_FailsWithTimeoutAndTheRestSucceed()
    {
        using var kv = new FakeSecretProvider().Returns("fast", "ok").Hangs("slow");
        var profile = Create([Provider("kv")], [Variable("FAST", "kv", "fast"), Variable("SLOW", "kv", "slow")]);

        var result = await Resolve(
            HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)),
            profile,
            options: new ResolveOptions(4, TimeSpan.FromMilliseconds(150)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(VariableStatus.Resolved, result.Outcomes[0].Status);
        Assert.Equal(VariableStatus.Failed, result.Outcomes[1].Status);
        Assert.Equal(ErrorKind.Timeout, result.Outcomes[1].Error.Kind);
    }

    [Fact]
    public async Task Resolve_CancelledByTheCaller_ThrowsInsteadOfReportingATimeout()
    {
        using var kv = new FakeSecretProvider().Hangs("slow");
        var profile = Create([Provider("kv")], [Variable("SLOW", "kv", "slow")]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Resolve_AProviderBug_IsAnInternalErrorNamingTheExceptionTypeAndFailsThatVariableOnly()
    {
        using var kv = new FakeSecretProvider()
            .Returns("ok", "fine")
            .Throws("boom", new InvalidOperationException("state was corrupt"));
        var profile = Create([Provider("kv")], [Variable("OK", "kv", "ok"), Variable("BOOM", "kv", "boom")]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(VariableStatus.Resolved, result.Outcomes[0].Status);
        var failed = result.Outcomes[1];
        Assert.Equal(ErrorKind.Internal, failed.Error.Kind);
        Assert.Contains("InvalidOperationException", failed.Error.Detail, StringComparison.Ordinal);
        Assert.Contains("state was corrupt", failed.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_ANetworkStyleException_IsProviderUnavailableSoAnOutageIsNotTakenForABug()
    {
        using var kv = new FakeSecretProvider().Throws("a", new IOException("connection reset"));
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.ProviderUnavailable, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task Resolve_ACancellationThatIsNotOurs_IsNotReportedAsATimeout()
    {
        using var kv = new FakeSecretProvider().Throws("a", new OperationCanceledException("the provider gave up"));
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderUnavailable, error.Kind);
        Assert.DoesNotContain("within", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_AnEmptySecret_IsMissingForARequiredVariable()
    {
        using var kv = new FakeSecretProvider().Returns("db-password", string.Empty);
        var profile = Create([Provider("kv")], [Variable("DB_PASSWORD", "kv", "db-password")]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSatisfied);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(VariableStatus.Missing, outcome.Status);
        Assert.Equal(ErrorKind.SecretEmpty, outcome.Error.Kind);
        Assert.Contains("empty", outcome.Error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_AnEmptySecret_IsSkippedForAnOptionalVariable()
    {
        using var kv = new FakeSecretProvider().Returns("log-level", string.Empty);
        var profile = Create([Provider("kv")], [Variable("LOG_LEVEL", "kv", "log-level", required: false)]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSatisfied);
        Assert.Equal(VariableStatus.Skipped, Assert.Single(result.Outcomes).Status);
    }

    [Fact]
    public async Task Resolve_AnEmptySecretIsCaughtEvenWhenTheValueIsNotKept()
    {
        using var kv = new FakeSecretProvider().Returns("a", string.Empty);
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, ResolveMode.Verify, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.SecretEmpty, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task Resolve_AnUnsupportedSecret_IsAFailureEvenForAnOptionalVariable()
    {
        using var kv = new FakeSecretProvider().Fails("cfg#value", ErrorKind.UnsupportedSecretType, "not a JSON object");
        var profile = Create([Provider("kv")], [Variable("CFG", "kv", "cfg#value", required: false)]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSatisfied);
        Assert.Equal(VariableStatus.Failed, Assert.Single(result.Outcomes).Status);
    }

    [Fact]
    public async Task Resolve_EveryCreationErrorIsShownNotJustTheFirst()
    {
        var factory = new FakeSecretProviderFactory(FakeType, _ => Result<ISecretProvider>.Failure(
        [
            new Error(ErrorKind.ProviderMisconfigured, "kv", "Unknown setting 'adress'."),
            new Error(ErrorKind.ProviderMisconfigured, "kv", "The 'kv' setting must be 1 or 2."),
        ]));
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await Resolve(HandlerFor(factory), profile, cancellationToken: TestContext.Current.CancellationToken);

        var detail = Assert.Single(result.Errors).Detail;
        Assert.Contains("adress", detail, StringComparison.Ordinal);
        Assert.Contains("must be 1 or 2", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_AFactoryThatThrows_IsReportedAndTheProvidersAlreadyBuiltAreStillDisposed()
    {
        var built = new FakeSecretProvider().Returns("a", "1");
        var good = FakeSecretProviderFactory.Returning("fake-good", built);
        var bad = new FakeSecretProviderFactory("fake-bad", _ => throw new InvalidOperationException("factory exploded"));
        var profile = Create(
            [Provider("good", "fake-good"), Provider("bad", "fake-bad")],
            [Variable("A", "good", "a"), Variable("B", "bad", "b")]);

        var result = await Resolve(HandlerFor(good, bad), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(VariableStatus.Resolved, result.Outcomes[0].Status);
        Assert.Equal(ErrorKind.Internal, result.Outcomes[1].Error.Kind);
        Assert.Contains("factory exploded", result.Outcomes[1].Error.Detail, StringComparison.Ordinal);
        Assert.True(built.IsDisposed);
    }

    [Fact]
    public async Task Resolve_AProviderWhoseDisposeThrows_DoesNotBreakTheResultOrTheOthersCleanup()
    {
        var noisy = new FakeSecretProvider { ThrowOnDispose = true }.Returns("a", "1");
        var quiet = new FakeSecretProvider().Returns("b", "2");
        var profile = Create(
            [Provider("noisy", "fake-noisy"), Provider("quiet", "fake-quiet")],
            [Variable("A", "noisy", "a"), Variable("B", "quiet", "b")]);

        var result = await Resolve(
            HandlerFor(FakeSecretProviderFactory.Returning("fake-noisy", noisy), FakeSecretProviderFactory.Returning("fake-quiet", quiet)),
            profile,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSatisfied);
        Assert.True(noisy.IsDisposed);
        Assert.True(quiet.IsDisposed);
    }

    [Fact]
    public async Task Resolve_AnAbsurdlyLargeTimeout_IsClampedInsteadOfCrashing()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await Resolve(
            HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)),
            profile,
            options: new ResolveOptions(4, TimeSpan.FromDays(400)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSatisfied);
    }

    [Fact]
    public async Task Resolve_DisposesTheProvidersItBuilt()
    {
        var kv = new FakeSecretProvider().Returns("a", "1");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(kv.IsDisposed);
    }

    [Fact]
    public async Task Resolve_VerifyMode_ChecksTheSecretsButDoesNotKeepTheValues()
    {
        using var kv = new FakeSecretProvider().Returns("db-password", "p1");
        var profile = Create([Provider("kv")], [Variable("DB_PASSWORD", "kv", "db-password")]);

        var result = await Resolve(HandlerFor(FakeSecretProviderFactory.Returning(FakeType, kv)), profile, ResolveMode.Verify, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(VariableStatus.Resolved, outcome.Status);
        Assert.Equal(string.Empty, outcome.Value.Reveal());
        Assert.Equal(1, kv.CallCount);
    }

    [Fact]
    public async Task Resolve_OfflineMode_BuildsProvidersButNeverCallsThem()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var factory = FakeSecretProviderFactory.Returning(FakeType, kv);
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await Resolve(HandlerFor(factory), profile, ResolveMode.Offline, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(0, kv.CallCount);
        Assert.Equal(VariableStatus.Unverified, Assert.Single(result.Outcomes).Status);
        Assert.True(result.IsSatisfied);
    }

    [Fact]
    public async Task Resolve_OfflineMode_StillSurfacesMisconfiguredProviders()
    {
        var factory = FakeSecretProviderFactory.Failing(FakeType, ErrorKind.ProviderMisconfigured, "uri is required");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await Resolve(HandlerFor(factory), profile, ResolveMode.Offline, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSatisfied);
        Assert.Equal(ErrorKind.ProviderMisconfigured, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task Resolve_ProfileWithoutVariables_IsSatisfiedAndBuildsNothing()
    {
        var factory = FakeSecretProviderFactory.Failing(FakeType, ErrorKind.ProviderMisconfigured);
        var profile = Create([Provider("kv")], []);

        var result = await Resolve(HandlerFor(factory), profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSatisfied);
        Assert.Empty(result.Outcomes);
        Assert.Equal(0, factory.CreateCalls);
    }
}
