using EnvSync.Application.Providers;
using EnvSync.Domain;
using EnvSync.TestSupport;

namespace EnvSync.Application.Tests;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void Create_KnownType_DelegatesToItsFactory()
    {
        using var provider = new FakeSecretProvider();
        var factory = FakeSecretProviderFactory.Returning("azure-keyvault", provider);
        var registry = new ProviderRegistry([factory]);

        var result = registry.Create(TestProfiles.Provider("kv", "azure-keyvault"));

        Assert.True(result.IsSuccess);
        Assert.Same(provider, result.Value);
        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public void Create_TypeLookupIgnoresCase()
    {
        using var provider = new FakeSecretProvider();
        var registry = new ProviderRegistry([FakeSecretProviderFactory.Returning("aws-secrets", provider)]);

        var result = registry.Create(TestProfiles.Provider("aws", "AWS-Secrets"));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Create_UnknownType_ReportsTheAliasAndTheRegisteredTypes()
    {
        using var provider = new FakeSecretProvider();
        var registry = new ProviderRegistry([FakeSecretProviderFactory.Returning("azure-keyvault", provider)]);

        var result = registry.Create(TestProfiles.Provider("db", "oracle-wallet"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderUnknown, error.Kind);
        Assert.Equal("db", error.Subject);
        Assert.Contains("oracle-wallet", error.Detail, StringComparison.Ordinal);
        Assert.Contains("azure-keyvault", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_FactoryFailure_IsPropagatedUntouched()
    {
        var registry = new ProviderRegistry([FakeSecretProviderFactory.Failing("hashicorp-vault", ErrorKind.ProviderMisconfigured, "address is required")]);

        var result = registry.Create(TestProfiles.Provider("vault", "hashicorp-vault"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Equal("address is required", error.Detail);
    }

    [Fact]
    public void IsRegistered_ReflectsTheRegisteredFactories()
    {
        using var provider = new FakeSecretProvider();
        var registry = new ProviderRegistry([FakeSecretProviderFactory.Returning("aws-secrets", provider)]);

        Assert.True(registry.IsRegistered("aws-secrets"));
        Assert.True(registry.IsRegistered("AWS-SECRETS"));
        Assert.False(registry.IsRegistered("azure-keyvault"));
    }

    [Fact]
    public void Constructor_TwoFactoriesForTheSameType_Throws()
    {
        using var provider = new FakeSecretProvider();
        var first = FakeSecretProviderFactory.Returning("aws-secrets", provider);
        var second = FakeSecretProviderFactory.Returning("AWS-Secrets", provider);

        Assert.Throws<ArgumentException>(() => new ProviderRegistry([first, second]));
    }
}
