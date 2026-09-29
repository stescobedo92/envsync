using System.Net;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;
using EnvSync.Providers.Vault;
using EnvSync.TestSupport;

namespace EnvSync.Providers.Vault.Tests;

public sealed class VaultProviderFactoryTests
{
    private const string OkBody = """{"data":{"data":{"k":"v"}}}""";

    private sealed class Environment
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Environment With(string name, string value)
        {
            _values[name] = value;
            return this;
        }

        public string? Get(string name) => _values.GetValueOrDefault(name);
    }

    private static ProviderDefinition Definition(params (string Key, string Value)[] settings) =>
        new("vault", "hashicorp-vault", settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase));

    private static VaultProviderFactory FactoryFor(Environment environment, string homeDirectory, StubHttpMessageHandler? handler = null) =>
        new(environment.Get, homeDirectory, handler is null ? null : () => handler);

    private static async Task<RecordedRequest> RequestMadeBy(ISecretProvider provider, StubHttpMessageHandler handler)
    {
        await provider.GetAsync(new SecretReference("app", "k"), TestContext.Current.CancellationToken);
        return Assert.Single(handler.Requests);
    }

    [Fact]
    public void Type_IsHashicorpVault()
    {
        Assert.Equal("hashicorp-vault", new VaultProviderFactory().Type);
    }

    [Fact]
    public async Task Create_ValidSettingsAndTokenFromTheEnvironment_BuildsAWorkingProvider()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);
        var environment = new Environment().With("VAULT_TOKEN", "tok-env");

        var result = FactoryFor(environment, home.Path, handler)
            .Create(Definition(("address", "https://vault.example.com:8200"), ("namespace", "team-a"), ("mount", "kv"), ("kv", "2")));

        Assert.True(result.IsSuccess);
        var request = await RequestMadeBy(result.Value, handler);
        Assert.Equal("https://vault.example.com:8200/v1/kv/data/app", request.Uri.AbsoluteUri);
        Assert.Equal("tok-env", request.Headers["X-Vault-Token"]);
        Assert.Equal("team-a", request.Headers["X-Vault-Namespace"]);
    }

    [Fact]
    public async Task Create_Defaults_AreMountSecretAndKvVersion2()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path, handler)
            .Create(Definition(("address", "https://vault.example.com")));

        var request = await RequestMadeBy(result.Value, handler);
        Assert.Equal("https://vault.example.com/v1/secret/data/app", request.Uri.AbsoluteUri);
        Assert.DoesNotContain("X-Vault-Namespace", request.Headers.Keys);
    }

    [Fact]
    public async Task Create_KvVersion1_UsesTheVersion1Path()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":{"k":"v"}}""");

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path, handler)
            .Create(Definition(("address", "https://vault.example.com"), ("kv", "1")));

        var request = await RequestMadeBy(result.Value, handler);
        Assert.Equal("https://vault.example.com/v1/secret/app", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Create_AddressAndNamespaceFallBackToTheEnvironment()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);
        var environment = new Environment()
            .With("VAULT_TOKEN", "t")
            .With("VAULT_ADDR", "https://from-env.example.com:8200")
            .With("VAULT_NAMESPACE", "env-ns");

        var result = FactoryFor(environment, home.Path, handler).Create(Definition());

        var request = await RequestMadeBy(result.Value, handler);
        Assert.StartsWith("https://from-env.example.com:8200/", request.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("env-ns", request.Headers["X-Vault-Namespace"]);
    }

    [Fact]
    public async Task Create_SettingsWinOverTheEnvironment()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);
        var environment = new Environment()
            .With("VAULT_TOKEN", "t")
            .With("VAULT_ADDR", "https://from-env.example.com")
            .With("VAULT_NAMESPACE", "env-ns");

        var result = FactoryFor(environment, home.Path, handler)
            .Create(Definition(("address", "https://from-manifest.example.com"), ("namespace", "manifest-ns")));

        var request = await RequestMadeBy(result.Value, handler);
        Assert.StartsWith("https://from-manifest.example.com/", request.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("manifest-ns", request.Headers["X-Vault-Namespace"]);
    }

    [Fact]
    public void Create_MissingAddress_IsMisconfiguredAndMentionsBothWaysToSetIt()
    {
        using var home = new TempHome();

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path).Create(Definition());

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Equal("vault", error.Subject);
        Assert.Contains("address", error.Detail, StringComparison.Ordinal);
        Assert.Contains("VAULT_ADDR", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("vault.example.com:8200")]
    [InlineData("ftp://vault.example.com")]
    [InlineData("/relative/path")]
    public void Create_InvalidAddress_IsMisconfigured(string address)
    {
        using var home = new TempHome();

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path).Create(Definition(("address", address)));

        Assert.Equal(ErrorKind.ProviderMisconfigured, Assert.Single(result.Errors).Kind);
    }

    [Theory]
    [InlineData("http://vault.example.com:8200")]
    [InlineData("http://10.0.0.5:8200")]
    public void Create_PlainHttpToARemoteHost_IsRefusedBecauseTheTokenWouldTravelInTheClear(string address)
    {
        using var home = new TempHome();

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path).Create(Definition(("address", address)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("https", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://localhost:8200")]
    [InlineData("http://127.0.0.1:8200")]
    [InlineData("http://[::1]:8200")]
    public void Create_PlainHttpToLoopback_IsAllowedForLocalDevServers(string address)
    {
        using var home = new TempHome();

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path).Create(Definition(("address", address)));

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("v2")]
    [InlineData("")]
    public void Create_KvVersionOtherThan1Or2_IsMisconfigured(string kv)
    {
        using var home = new TempHome();

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path)
            .Create(Definition(("address", "https://vault.example.com"), ("kv", kv)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("kv", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_UnknownSetting_IsMisconfiguredSoTyposAreCaught()
    {
        using var home = new TempHome();

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path)
            .Create(Definition(("address", "https://vault.example.com"), ("adress", "typo")));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("adress", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_EmptyMount_IsMisconfigured()
    {
        using var home = new TempHome();

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path)
            .Create(Definition(("address", "https://vault.example.com"), ("mount", "/")));

        Assert.Equal(ErrorKind.ProviderMisconfigured, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task Create_TokenFallsBackToTheVaultTokenFileInTheHomeDirectory()
    {
        using var home = new TempHome();
        home.WriteToken("tok-from-file\n");
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);

        var result = FactoryFor(new Environment(), home.Path, handler).Create(Definition(("address", "https://vault.example.com")));

        var request = await RequestMadeBy(result.Value, handler);
        Assert.Equal("tok-from-file", request.Headers["X-Vault-Token"]);
    }

    [Fact]
    public async Task Create_TheEnvironmentTokenWinsOverTheFile()
    {
        using var home = new TempHome();
        home.WriteToken("tok-from-file");
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "tok-env"), home.Path, handler)
            .Create(Definition(("address", "https://vault.example.com")));

        var request = await RequestMadeBy(result.Value, handler);
        Assert.Equal("tok-env", request.Headers["X-Vault-Token"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public void Create_NoUsableToken_IsAuthenticationFailedAndSaysWhereToSetOne(string fileContent)
    {
        using var home = new TempHome();
        home.WriteToken(fileContent);

        var result = FactoryFor(new Environment(), home.Path).Create(Definition(("address", "https://vault.example.com")));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("VAULT_TOKEN", error.Detail, StringComparison.Ordinal);
        Assert.Contains("vault login", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_NoTokenFileAtAll_IsAuthenticationFailed()
    {
        using var home = new TempHome();

        var result = FactoryFor(new Environment(), home.Path).Create(Definition(("address", "https://vault.example.com")));

        Assert.Equal(ErrorKind.AuthenticationFailed, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public void Create_ErrorsNeverContainTheToken()
    {
        using var home = new TempHome();
        const string token = "hvs.do-not-leak";

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", token), home.Path)
            .Create(Definition(("address", "http://vault.example.com")));

        Assert.All(result.Errors, error => Assert.DoesNotContain(token, error.Detail + error.Subject, StringComparison.Ordinal));
    }

    [Fact]
    public void Create_DoesNotTouchTheNetwork()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);

        FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path, handler).Create(Definition(("address", "https://vault.example.com")));

        Assert.Empty(handler.Requests);
    }
}
