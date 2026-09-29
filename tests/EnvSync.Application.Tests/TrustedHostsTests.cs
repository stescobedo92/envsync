using EnvSync.Application.Providers;

namespace EnvSync.Application.Tests;

public sealed class TrustedHostsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingConfigured_TrustsNothing(string? list)
    {
        Assert.False(TrustedHosts.Parse(list).IsTrusted("vault.example.com"));
    }

    [Fact]
    public void AnExactHost_IsTrustedIgnoringCase()
    {
        var trusted = TrustedHosts.Parse("vault.corp.example.com");

        Assert.True(trusted.IsTrusted("vault.corp.example.com"));
        Assert.True(trusted.IsTrusted("VAULT.CORP.EXAMPLE.COM"));
        Assert.False(trusted.IsTrusted("other.corp.example.com"));
    }

    [Theory]
    [InlineData("a.example.com,b.example.com")]
    [InlineData("a.example.com;b.example.com")]
    [InlineData(" a.example.com , b.example.com ")]
    [InlineData("a.example.com b.example.com")]
    public void SeveralHostsMayBeListed(string list)
    {
        var trusted = TrustedHosts.Parse(list);

        Assert.True(trusted.IsTrusted("a.example.com"));
        Assert.True(trusted.IsTrusted("b.example.com"));
        Assert.False(trusted.IsTrusted("c.example.com"));
    }

    [Fact]
    public void AWildcardTrustsSubdomainsButNotTheBareDomainNorLookalikes()
    {
        var trusted = TrustedHosts.Parse("*.corp.example.com");

        Assert.True(trusted.IsTrusted("vault.corp.example.com"));
        Assert.True(trusted.IsTrusted("a.b.corp.example.com"));
        Assert.False(trusted.IsTrusted("corp.example.com"));
        Assert.False(trusted.IsTrusted("evilcorp.example.com"));
        Assert.False(trusted.IsTrusted("corp.example.com.evil.com"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.")]
    [InlineData(".")]
    public void ALoneWildcardCannotTrustEverything(string list)
    {
        Assert.False(TrustedHosts.Parse(list).IsTrusted("anything.example.com"));
    }

    [Fact]
    public void FromEnvironment_ReadsTheDocumentedVariable()
    {
        var trusted = TrustedHosts.FromEnvironment(name => name == "ENVSYNC_TRUSTED_HOSTS" ? "vault.example.com" : null);

        Assert.True(trusted.IsTrusted("vault.example.com"));
        Assert.Equal("ENVSYNC_TRUSTED_HOSTS", TrustedHosts.VariableName);
    }
}
