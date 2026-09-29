using EnvSync.Application.Export;
using EnvSync.Application.Providers;
using EnvSync.Application.Resolution;
using EnvSync.Domain;
using EnvSync.TestSupport;
using static EnvSync.TestSupport.TestProfiles;

namespace EnvSync.Application.Tests;

public sealed class ExportEnvironmentQueryHandlerTests
{
    private static readonly ResolveOptions Sane = new(4, TimeSpan.FromSeconds(10));

    private static ExportEnvironmentQueryHandler HandlerFor(FakeSecretProvider provider, params FakeShellEmitter[] emitters) =>
        new(
            new ResolveEnvironmentQueryHandler(new ProviderRegistry([FakeSecretProviderFactory.Returning(FakeType, provider)])),
            emitters);

    [Fact]
    public async Task Export_WritesEveryResolvedVariableInManifestOrder()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1").Returns("b", "2");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a"), Variable("B", "kv", "b")]);

        using var result = await HandlerFor(kv, new FakeShellEmitter())
            .HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.Bash), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("A=1\nB=2\n", result.Script.Span.ToString());
    }

    [Fact]
    public async Task Export_SkippedOptionalVariables_AreNotEmitted()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var profile = Create(
            [Provider("kv")],
            [Variable("A", "kv", "a"), Variable("OPTIONAL", "kv", "nope", required: false)]);

        using var result = await HandlerFor(kv, new FakeShellEmitter())
            .HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.Bash), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("A=1\n", result.Script.Span.ToString());
    }

    [Fact]
    public async Task Export_WhenARequiredKeyIsMissing_ProducesNoScriptAtAll()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a"), Variable("B", "kv", "missing")]);

        using var result = await HandlerFor(kv, new FakeShellEmitter())
            .HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.Bash), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.Script.IsEmpty);
        Assert.False(result.Resolution.IsSatisfied);
    }

    [Fact]
    public async Task Export_WhenTheShellRefusesAValue_ReportsItAndProducesNoScript()
    {
        using var kv = new FakeSecretProvider().Returns("a", "safe").Returns("b", "has%percent");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a"), Variable("B", "kv", "b")]);
        var emitter = new FakeShellEmitter(ShellKind.Cmd) { Rejects = value => value.Contains('%', StringComparison.Ordinal) };

        using var result = await HandlerFor(kv, emitter)
            .HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.Cmd), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.Script.IsEmpty);
        var error = Assert.Single(result.EmitErrors);
        Assert.Equal(ErrorKind.UnsupportedValueForShell, error.Kind);
        Assert.Equal("B", error.Subject);
        Assert.DoesNotContain("has%percent", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_WhenARequiredKeyIsMissing_OffersAFailureScriptCarryingTheExitCode()
    {
        using var kv = new FakeSecretProvider();
        var profile = Create([Provider("kv")], [Variable("A", "kv", "missing")]);

        using var result = await HandlerFor(kv, new FakeShellEmitter())
            .HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.Bash), TestContext.Current.CancellationToken);

        Assert.Equal("FAIL 11\n", result.FailureScript);
    }

    [Fact]
    public async Task Export_WhenTheShellRefusesAValue_TheFailureScriptCarriesTheUnsupportedValueCode()
    {
        using var kv = new FakeSecretProvider().Returns("a", "has%percent");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);
        var emitter = new FakeShellEmitter(ShellKind.Cmd) { Rejects = value => value.Contains('%', StringComparison.Ordinal) };

        using var result = await HandlerFor(kv, emitter)
            .HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.Cmd), TestContext.Current.CancellationToken);

        Assert.Equal("FAIL 14\n", result.FailureScript);
    }

    [Fact]
    public async Task Export_OnSuccess_HasNoFailureScript()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        using var result = await HandlerFor(kv, new FakeShellEmitter())
            .HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.Bash), TestContext.Current.CancellationToken);

        Assert.Empty(result.FailureScript);
    }

    [Fact]
    public async Task Export_UsesTheEmitterOfTheRequestedShell()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);
        var handler = HandlerFor(kv, new FakeShellEmitter(ShellKind.Bash), new FakeShellEmitter(ShellKind.PowerShell) { Rejects = _ => true });

        using var bash = await handler.HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.Bash), TestContext.Current.CancellationToken);
        using var pwsh = await handler.HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.PowerShell), TestContext.Current.CancellationToken);

        Assert.True(bash.IsSuccess);
        Assert.False(pwsh.IsSuccess);
    }

    [Fact]
    public async Task Export_NoEmitterForTheShell_IsAProgrammingError()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);
        var handler = HandlerFor(kv, new FakeShellEmitter(ShellKind.Bash));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.Cmd), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Export_DisposingTheResultTwice_IsHarmless()
    {
        using var kv = new FakeSecretProvider().Returns("a", "1");
        var profile = Create([Provider("kv")], [Variable("A", "kv", "a")]);

        var result = await HandlerFor(kv, new FakeShellEmitter())
            .HandleAsync(new ExportEnvironmentQuery(profile, Sane, ShellKind.Bash), TestContext.Current.CancellationToken);

        result.Dispose();
        result.Dispose();
    }
}
