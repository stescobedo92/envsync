using Azure;
using Azure.Identity;
using EnvSync.Domain;
using EnvSync.Providers.Azure;

namespace EnvSync.Providers.Azure.Tests;

public sealed class AzureKeyVaultSecretProviderTests
{
    private static ValueTask<Result<SecretValue>> Get(FakeSecretClient client, string path, string? field = null, CancellationToken? token = null) =>
        new AzureKeyVaultSecretProvider(client).GetAsync(new SecretReference(path, field), token ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task Get_ReturnsTheSecretValueAsIs()
    {
        var client = FakeSecretClient.Returning("p@ss w0rd");

        var result = await Get(client, "db-password");

        Assert.True(result.IsSuccess);
        Assert.Equal("p@ss w0rd", result.Value.Reveal());
    }

    [Fact]
    public async Task Get_AsksForTheLatestVersionOfExactlyThatName()
    {
        var client = FakeSecretClient.Returning("v");

        await Get(client, "db-password");

        var call = Assert.Single(client.Calls);
        Assert.Equal("db-password", call.Name);
        Assert.Null(call.Version);
    }

    [Fact]
    public async Task Get_WithAField_PicksItFromAJsonSecret()
    {
        var client = FakeSecretClient.Returning("""{"user":"admin","password":"s3cret"}""");

        var result = await Get(client, "db-credentials", "password");

        Assert.Equal("s3cret", result.Value.Reveal());
    }

    [Fact]
    public async Task Get_WithAFieldTheSecretLacks_IsFieldNotFound()
    {
        var client = FakeSecretClient.Returning("""{"user":"admin"}""");

        var result = await Get(client, "db-credentials", "password");

        Assert.Equal(ErrorKind.FieldNotFound, Assert.Single(result.Errors).Kind);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("db-password")]
    [InlineData("Db-Password-2")]
    public async Task Get_ValidNames_ReachTheVault(string name)
    {
        var client = FakeSecretClient.Returning("v");

        var result = await Get(client, name);

        Assert.True(result.IsSuccess);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task Get_TheLongestValidNameHas127Characters()
    {
        var client = FakeSecretClient.Returning("v");

        Assert.True((await Get(client, new string('a', 127))).IsSuccess);
        Assert.False((await Get(client, new string('a', 128))).IsSuccess);
    }

    [Theory]
    [InlineData("DB_PASSWORD")]
    [InlineData("has space")]
    [InlineData("path/to/secret")]
    [InlineData("dot.ted")]
    [InlineData("ünïcode")]
    public async Task Get_InvalidNames_AreRejectedBeforeCallingTheVaultAndExplainTheRule(string name)
    {
        var client = FakeSecretClient.Returning("v");

        var result = await Get(client, name);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Equal(name, error.Subject);
        Assert.Contains("letters, digits and '-'", error.Detail, StringComparison.Ordinal);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Get_Status404_IsSecretNotFound()
    {
        var client = FakeSecretClient.Throwing(new RequestFailedException(404, "not found", "SecretNotFound", null));

        var error = Assert.Single((await Get(client, "db-password")).Errors);

        Assert.Equal(ErrorKind.SecretNotFound, error.Kind);
        Assert.Equal("db-password", error.Subject);
    }

    [Fact]
    public async Task Get_ADisabledSecret_IsSecretNotFoundAndSaysSo()
    {
        var client = FakeSecretClient.Throwing(new RequestFailedException(403, "disabled", "SecretDisabled", null));

        var error = Assert.Single((await Get(client, "db-password")).Errors);

        Assert.Equal(ErrorKind.SecretNotFound, error.Kind);
        Assert.Contains("disabled", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    public async Task Get_Status401Or403_IsAuthenticationFailedWithTheRbacHint(int status, string code)
    {
        var client = FakeSecretClient.Throwing(new RequestFailedException(status, "denied", code, null));

        var error = Assert.Single((await Get(client, "db-password")).Errors);

        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("Key Vault Secrets User", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Get_OtherFailures_AreProviderUnavailableAndNameTheStatus(int status)
    {
        var client = FakeSecretClient.Throwing(new RequestFailedException(status, "boom", "SomeCode", null));

        var error = Assert.Single((await Get(client, "db-password")).Errors);

        Assert.Equal(ErrorKind.ProviderUnavailable, error.Kind);
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_NoUsableCredential_IsAuthenticationFailedWithTheLoginHint()
    {
        var client = FakeSecretClient.Throwing(new CredentialUnavailableException("EnvironmentCredential authentication unavailable.\r\nSecond line that should not be shown."));

        var error = Assert.Single((await Get(client, "db-password")).Errors);

        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("az login", error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Second line", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_AFailedAuthentication_IsAuthenticationFailed()
    {
        var client = FakeSecretClient.Throwing(new AuthenticationFailedException("AADSTS700016: application not found"));

        var error = Assert.Single((await Get(client, "db-password")).Errors);

        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("AADSTS700016", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ErrorDetailsNeverEchoTheServiceResponseBody()
    {
        var client = FakeSecretClient.Throwing(new RequestFailedException(500, "Service request failed.\r\nContent:\r\n{\"value\":\"hunter2-do-not-leak\"}", "InternalError", null));

        var error = Assert.Single((await Get(client, "db-password")).Errors);

        Assert.DoesNotContain("hunter2-do-not-leak", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Cancelled_Throws()
    {
        var client = new FakeSecretClient(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null!;
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Get(client, "db-password", token: cts.Token));
    }
}
