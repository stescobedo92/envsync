using Azure.Security.KeyVault.Secrets;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;
using EnvSync.Providers.Azure;

namespace EnvSync.Providers.Azure.Tests;

public sealed class AzureKeyVaultProviderFactoryTests
{
    private static ProviderDefinition Definition(params (string Key, string Value)[] settings) =>
        new("kv", "azure-keyvault", settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase));

    private static (AzureKeyVaultProviderFactory Factory, List<Uri> Uris) Recording()
    {
        var uris = new List<Uri>();
        var factory = new AzureKeyVaultProviderFactory(uri =>
        {
            uris.Add(uri);
            return FakeSecretClient.Returning("v");
        });

        return (factory, uris);
    }

    [Fact]
    public void Type_IsAzureKeyVault()
    {
        Assert.Equal("azure-keyvault", new AzureKeyVaultProviderFactory().Type);
    }

    [Fact]
    public void Create_ValidUri_BuildsAClientForThatVault()
    {
        var (factory, uris) = Recording();

        var result = factory.Create(Definition(("uri", "https://my-kv.vault.azure.net")));

        Assert.True(result.IsSuccess);
        Assert.Equal(new Uri("https://my-kv.vault.azure.net"), Assert.Single(uris));
    }

    [Fact]
    public async Task Create_TheProviderItReturnsReadsThroughThatClient()
    {
        var (factory, _) = Recording();

        var provider = factory.Create(Definition(("uri", "https://my-kv.vault.azure.net"))).Value;
        var secret = await provider.GetAsync(new SecretReference("db-password", null), TestContext.Current.CancellationToken);

        Assert.Equal("v", secret.Value.Reveal());
    }

    [Fact]
    public void Create_SettingNamesIgnoreCase()
    {
        var (factory, _) = Recording();

        Assert.True(factory.Create(Definition(("URI", "https://my-kv.vault.azure.net"))).IsSuccess);
    }

    [Fact]
    public void Create_MissingUri_IsMisconfigured()
    {
        var (factory, uris) = Recording();

        var result = factory.Create(Definition());

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Equal("kv", error.Subject);
        Assert.Contains("uri", error.Detail, StringComparison.Ordinal);
        Assert.Empty(uris);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("my-kv.vault.azure.net")]
    [InlineData("/relative")]
    [InlineData("")]
    public void Create_InvalidUri_IsMisconfigured(string uri)
    {
        var (factory, _) = Recording();

        var result = factory.Create(Definition(("uri", uri)));

        Assert.Equal(ErrorKind.ProviderMisconfigured, Assert.Single(result.Errors).Kind);
    }

    [Theory]
    [InlineData("http://my-kv.vault.azure.net")]
    [InlineData("ftp://my-kv.vault.azure.net")]
    public void Create_NonHttpsUri_IsRefused(string uri)
    {
        var (factory, uris) = Recording();

        var result = factory.Create(Definition(("uri", uri)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("https", error.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(uris);
    }

    [Fact]
    public void Create_UnknownSetting_IsMisconfiguredSoTyposAreCaught()
    {
        var (factory, _) = Recording();

        var result = factory.Create(Definition(("uri", "https://my-kv.vault.azure.net"), ("url", "typo")));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("url", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WithTheDefaultClientFactory_BuildsWithoutTouchingTheNetwork()
    {
        // DefaultAzureCredential only resolves a token when the first request is made.
        var result = new AzureKeyVaultProviderFactory().Create(Definition(("uri", "https://my-kv.vault.azure.net")));

        Assert.True(result.IsSuccess);
        Assert.IsAssignableFrom<ISecretProvider>(result.Value);
    }
}
