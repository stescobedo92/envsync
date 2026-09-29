using System.Net;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;
using EnvSync.Providers.Vault;
using EnvSync.TestSupport;

namespace EnvSync.Providers.Vault.Tests;

public sealed class VaultProviderFactoryTests
{
    private const string OkBody = """{"data":{"data":{"k":"v"}}}""";
    private const string Address = "https://vault.example.com";

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

    /// <summary>An environment where the user's own Vault is <see cref="Address"/>, so a manifest naming it is trusted.</summary>
    private static Environment Users(string vaultAddress = Address) =>
        new Environment().With("VAULT_TOKEN", "t").With("VAULT_ADDR", vaultAddress);

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

        var result = FactoryFor(Users("https://vault.example.com:8200"), home.Path, handler)
            .Create(Definition(("address", "https://vault.example.com:8200"), ("namespace", "team-a"), ("mount", "kv"), ("kv", "2")));

        Assert.True(result.IsSuccess);
        var request = await RequestMadeBy(result.Value, handler);
        Assert.Equal("https://vault.example.com:8200/v1/kv/data/app", request.Uri.AbsoluteUri);
        Assert.Equal("t", request.Headers["X-Vault-Token"]);
        Assert.Equal("team-a", request.Headers["X-Vault-Namespace"]);
    }

    [Fact]
    public async Task Create_Defaults_AreMountSecretAndKvVersion2()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);

        var result = FactoryFor(Users(), home.Path, handler).Create(Definition(("address", Address)));

        var request = await RequestMadeBy(result.Value, handler);
        Assert.Equal("https://vault.example.com/v1/secret/data/app", request.Uri.AbsoluteUri);
        Assert.DoesNotContain("X-Vault-Namespace", request.Headers.Keys);
    }

    [Fact]
    public async Task Create_KvVersion1_UsesTheVersion1Path()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":{"k":"v"}}""");

        var result = FactoryFor(Users(), home.Path, handler).Create(Definition(("address", Address), ("kv", "1")));

        var request = await RequestMadeBy(result.Value, handler);
        Assert.Equal("https://vault.example.com/v1/secret/app", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Create_WithoutAManifestAddress_TheUsersOwnVaultAddrAndNamespaceApply()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);
        var environment = Users("https://from-env.example.com:8200").With("VAULT_NAMESPACE", "env-ns");

        var result = FactoryFor(environment, home.Path, handler).Create(Definition());

        var request = await RequestMadeBy(result.Value, handler);
        Assert.StartsWith("https://from-env.example.com:8200/", request.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("env-ns", request.Headers["X-Vault-Namespace"]);
    }

    [Fact]
    public async Task Create_TheManifestNamespaceBeatsTheEnvironmentOne()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);
        var environment = Users().With("VAULT_NAMESPACE", "env-ns");

        var result = FactoryFor(environment, home.Path, handler).Create(Definition(("address", Address), ("namespace", "manifest-ns")));

        var request = await RequestMadeBy(result.Value, handler);
        Assert.Equal("manifest-ns", request.Headers["X-Vault-Namespace"]);
    }

    // A manifest is repository content: anyone who can change it could point "address" at a server they control and receive the
    // token the user has for their own Vault. The token only ever goes where the user's environment says their Vault is.
    [Fact]
    public void Create_AManifestAddressThatIsNotTheUsersOwnVault_IsRefusedSoTheTokenIsNeverSentThere()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);
        const string token = "hvs.the-users-real-token";
        var environment = new Environment().With("VAULT_TOKEN", token).With("VAULT_ADDR", "https://my-real-vault.example.com");

        var result = FactoryFor(environment, home.Path, handler).Create(Definition(("address", "https://evil.example.net")));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("evil.example.net", error.Detail, StringComparison.Ordinal);
        Assert.Contains("VAULT_ADDR", error.Detail, StringComparison.Ordinal);
        Assert.Contains("ENVSYNC_TRUSTED_HOSTS", error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(token, error.Detail, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Create_AManifestAddressWithNoVaultAddrToConfirmIt_IsRefused()
    {
        using var home = new TempHome();

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", "t"), home.Path).Create(Definition(("address", Address)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("VAULT_ADDR", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://VAULT.example.com/")]
    [InlineData("https://vault.example.com/some/path")]
    public void Create_TheSameOriginAsVaultAddr_IsTrustedWhateverThePathOrCase(string manifestAddress)
    {
        using var home = new TempHome();

        var result = FactoryFor(Users(), home.Path).Create(Definition(("address", manifestAddress)));

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("https://vault.example.com:9200")]
    [InlineData("https://vault.example.com.evil.net")]
    public void Create_ADifferentPortOrHostThanVaultAddr_IsNotTheSameVault(string manifestAddress)
    {
        using var home = new TempHome();

        var result = FactoryFor(Users(), home.Path).Create(Definition(("address", manifestAddress)));

        Assert.Equal(ErrorKind.ProviderMisconfigured, Assert.Single(result.Errors).Kind);
    }

    [Theory]
    [InlineData("vault.corp.example.com")]
    [InlineData("*.corp.example.com")]
    public void Create_AHostTheUserDeclaredTrustedOutsideTheRepository_IsAccepted(string trusted)
    {
        using var home = new TempHome();
        var environment = new Environment().With("VAULT_TOKEN", "t").With("ENVSYNC_TRUSTED_HOSTS", trusted);

        var result = FactoryFor(environment, home.Path).Create(Definition(("address", "https://vault.corp.example.com")));

        Assert.True(result.IsSuccess);
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

        var result = FactoryFor(Users(), home.Path).Create(Definition(("address", address)));

        Assert.Equal(ErrorKind.ProviderMisconfigured, Assert.Single(result.Errors).Kind);
    }

    [Theory]
    [InlineData("http://vault.example.com:8200")]
    [InlineData("http://10.0.0.5:8200")]
    public void Create_PlainHttpToARemoteHost_IsRefusedBecauseTheTokenWouldTravelInTheClear(string address)
    {
        using var home = new TempHome();

        var result = FactoryFor(Users(address), home.Path).Create(Definition(("address", address)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("https", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://localhost:8200")]
    [InlineData("http://127.0.0.1:8200")]
    [InlineData("http://[::1]:8200")]
    public void Create_PlainHttpToLoopback_IsAllowedForLocalDevServersWithoutAnyConfirmation(string address)
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

        var result = FactoryFor(Users(), home.Path).Create(Definition(("address", Address), ("kv", kv)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("kv", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("namespace")]
    [InlineData("mount")]
    public void Create_ASettingThatIsPresentButBlank_IsAnErrorNotASilentFallback(string key)
    {
        using var home = new TempHome();
        var settings = new List<(string, string)> { ("address", Address) };
        settings.RemoveAll(s => s.Item1 == key);
        settings.Add((key, "  "));

        var result = FactoryFor(Users(), home.Path).Create(Definition([.. settings]));

        var error = result.Errors.First(e => e.Detail.Contains($"'{key}'", StringComparison.Ordinal));
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("empty", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_UnknownSetting_IsMisconfiguredSoTyposAreCaught()
    {
        using var home = new TempHome();

        var result = FactoryFor(Users(), home.Path).Create(Definition(("address", Address), ("adress", "typo")));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("adress", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_SeveralProblemsAtOnce_AreAllReported()
    {
        using var home = new TempHome();

        var result = FactoryFor(Users(), home.Path).Create(Definition(("address", Address), ("adress", "typo"), ("kv", "3")));

        Assert.Equal(2, result.Errors.Length);
    }

    [Fact]
    public void Create_EmptyMount_IsMisconfigured()
    {
        using var home = new TempHome();

        var result = FactoryFor(Users(), home.Path).Create(Definition(("address", Address), ("mount", "/")));

        Assert.Equal(ErrorKind.ProviderMisconfigured, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task Create_TokenFallsBackToTheVaultTokenFileInTheHomeDirectory()
    {
        using var home = new TempHome();
        home.WriteToken("tok-from-file\n");
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);

        var result = FactoryFor(new Environment().With("VAULT_ADDR", Address), home.Path, handler).Create(Definition());

        var request = await RequestMadeBy(result.Value, handler);
        Assert.Equal("tok-from-file", request.Headers["X-Vault-Token"]);
    }

    [Fact]
    public async Task Create_TheEnvironmentTokenWinsOverTheFile()
    {
        using var home = new TempHome();
        home.WriteToken("tok-from-file");
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);

        var result = FactoryFor(Users().With("VAULT_TOKEN", "tok-env"), home.Path, handler).Create(Definition());

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

        var result = FactoryFor(new Environment().With("VAULT_ADDR", Address), home.Path).Create(Definition());

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("VAULT_TOKEN", error.Detail, StringComparison.Ordinal);
        Assert.Contains("vault login", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_NoTokenFileAtAll_IsAuthenticationFailed()
    {
        using var home = new TempHome();

        var result = FactoryFor(new Environment().With("VAULT_ADDR", Address), home.Path).Create(Definition());

        Assert.Equal(ErrorKind.AuthenticationFailed, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public void Create_ATokenFileThatExistsButCannotBeRead_SaysSoInsteadOfClaimingThereIsNone()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(Path.Combine(home.Path, ".vault-token")); // reading a directory as a file fails

        var result = FactoryFor(new Environment().With("VAULT_ADDR", Address), home.Path).Create(Definition());

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("could not be read", error.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No Vault token was found", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_AnEmptyHomeDirectory_IsNeverTurnedIntoTheCurrentDirectory()
    {
        var result = FactoryFor(new Environment().With("VAULT_ADDR", Address), homeDirectory: string.Empty).Create(Definition());

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("No Vault token was found", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ErrorsNeverContainTheToken()
    {
        using var home = new TempHome();
        const string token = "hvs.do-not-leak";

        var result = FactoryFor(new Environment().With("VAULT_TOKEN", token).With("VAULT_ADDR", "https://mine.example.com"), home.Path)
            .Create(Definition(("address", "http://vault.example.com"), ("kv", "9")));

        Assert.All(result.Errors, error => Assert.DoesNotContain(token, error.Detail + error.Subject, StringComparison.Ordinal));
    }

    [Fact]
    public void Create_DoesNotTouchTheNetwork()
    {
        using var home = new TempHome();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody);

        FactoryFor(Users(), home.Path, handler).Create(Definition(("address", Address)));

        Assert.Empty(handler.Requests);
    }
}
