using System.Net;
using EnvSync.Domain;
using EnvSync.Providers.Vault;
using EnvSync.TestSupport;

namespace EnvSync.Providers.Vault.Tests;

public sealed class VaultSecretProviderTests
{
    private const string Token = "hvs.super-secret-token";
    private static readonly Uri Address = new("https://vault.example.com:8200");

    private const string Kv2Body = """{"data":{"data":{"password":"p@ss w0rd","port":5432,"enabled":true,"nothing":null,"nested":{"a":1}},"metadata":{"version":3}}}""";
    private const string Kv1Body = """{"data":{"password":"p@ss w0rd"},"lease_duration":0}""";

    private static VaultSecretProvider ProviderFor(
        StubHttpMessageHandler handler,
        int kvVersion = 2,
        string mount = "secret",
        string? vaultNamespace = null) =>
        new(new HttpClient(handler), new VaultOptions(Address, vaultNamespace, mount, kvVersion, new SecretValue(Token)));

    private static ValueTask<Result<SecretValue>> Get(VaultSecretProvider provider, string path, string? field, CancellationToken? token = null) =>
        provider.GetAsync(new SecretReference(path, field), token ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task Get_Kv2_ReadsTheFieldFromTheDataEnvelope()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body);

        var result = await Get(ProviderFor(handler), "app/db", "password");

        Assert.True(result.IsSuccess);
        Assert.Equal("p@ss w0rd", result.Value.Reveal());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://vault.example.com:8200/v1/secret/data/app/db", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Get_Kv1_ReadsTheFieldFromTheTopLevelData()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv1Body);

        var result = await Get(ProviderFor(handler, kvVersion: 1), "app/db", "password");

        Assert.Equal("p@ss w0rd", result.Value.Reveal());
        Assert.Equal("https://vault.example.com:8200/v1/secret/app/db", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Get_SendsTheTokenAndTheNamespaceAsHeaders()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body);

        await Get(ProviderFor(handler, vaultNamespace: "team-a"), "app/db", "password");

        var headers = Assert.Single(handler.Requests).Headers;
        Assert.Equal(Token, headers["X-Vault-Token"]);
        Assert.Equal("team-a", headers["X-Vault-Namespace"]);
    }

    [Fact]
    public async Task Get_WithoutANamespace_SendsNoNamespaceHeader()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body);

        await Get(ProviderFor(handler), "app/db", "password");

        Assert.DoesNotContain("X-Vault-Namespace", Assert.Single(handler.Requests).Headers.Keys);
    }

    [Theory]
    [InlineData("my app/db 1", "my%20app/db%201")]
    [InlineData("weird?name/x%y", "weird%3Fname/x%25y")]
    [InlineData("/leading//double/", "leading/double")]
    public async Task Get_EscapesEachPathSegmentAndDropsEmptyOnes(string path, string expectedTail)
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body);

        await Get(ProviderFor(handler), path, "password");

        Assert.Equal($"https://vault.example.com:8200/v1/secret/data/{expectedTail}", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Get_MountMayHaveSeveralSegments()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body);

        await Get(ProviderFor(handler, mount: "team/kv"), "app", "password");

        Assert.Equal("https://vault.example.com:8200/v1/team/kv/data/app", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("port", "5432")]
    [InlineData("enabled", "true")]
    public async Task Get_NumbersAndBooleansBecomeTheirLiteralText(string field, string expected)
    {
        var result = await Get(ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body)), "app", field);

        Assert.Equal(expected, result.Value.Reveal());
    }

    [Fact]
    public async Task Get_WithoutAField_IsFieldRequired()
    {
        var result = await Get(ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body)), "app/db", field: null);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.FieldRequired, error.Kind);
        Assert.Equal("app/db", error.Subject);
        Assert.Contains("#", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_FieldRequiredIsReportedWithoutCallingVault()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body);

        await Get(ProviderFor(handler), "app/db", field: null);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Get_MissingField_IsFieldNotFound()
    {
        var result = await Get(ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body)), "app", "absent");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.FieldNotFound, error.Kind);
        Assert.Contains("absent", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("nested")]
    public async Task Get_NonScalarField_IsAnUnsupportedSecretNotAMissingOne(string field)
    {
        var result = await Get(ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body)), "app", field);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.UnsupportedSecretType, error.Kind);
        Assert.Contains("scalar", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // Vault answers 404 both for a secret that does not exist (empty "errors") and for a route that does not exist, such as a wrong
    // mount or kv version ("no handler for route ..."). The second is a configuration mistake, not a missing key.
    [Fact]
    public async Task Get_A404ThatNamesAnUnknownRoute_IsMisconfigurationNotAMissingSecret()
    {
        const string body = """{"errors":["no handler for route 'secret/data/app'. route entry not found."]}""";

        var result = await Get(ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.NotFound, body)), "app", "password");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("no handler for route", error.Detail, StringComparison.Ordinal);
        Assert.Contains("mount", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"errors":[]}""")]
    public async Task Get_A404WithoutAnExplanation_IsAMissingSecret(string body)
    {
        var result = await Get(ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.NotFound, body)), "app", "password");

        Assert.Equal(ErrorKind.SecretNotFound, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task Get_ATransportFailure_ShowsTheInnerExceptionSoATlsProblemCanBeDiagnosed()
    {
        var failure = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new System.Security.Authentication.AuthenticationException("The remote certificate is invalid according to the validation procedure."));

        var result = await Get(ProviderFor(StubHttpMessageHandler.Throwing(failure)), "app", "password");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderUnavailable, error.Kind);
        Assert.Contains("remote certificate is invalid", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ABodyLargerThanASecretCouldBe_IsProviderUnavailableInsteadOfBeingBuffered()
    {
        var huge = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('a', 2 * 1024 * 1024)),
        }));

        var result = await Get(ProviderFor(huge), "app", "password");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderUnavailable, error.Kind);
        Assert.Contains("could not be read", error.Detail, StringComparison.Ordinal);
    }

    private sealed class FailingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new IOException("connection reset while reading the body");

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    [Fact]
    public async Task Get_ABodyThatFailsMidRead_IsProviderUnavailableNotAnExceptionThatEscapesTheProvider()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingContent() }));

        var result = await Get(ProviderFor(handler), "app", "password");

        Assert.Equal(ErrorKind.ProviderUnavailable, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task Get_A403WhoseBodyCannotBeRead_IsStillAuthenticationFailed()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new FailingContent() }));

        var result = await Get(ProviderFor(handler), "app", "password");

        Assert.Equal(ErrorKind.AuthenticationFailed, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task Get_Status404_IsSecretNotFound()
    {
        var result = await Get(ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.NotFound, """{"errors":[]}""")), "app/db", "password");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.SecretNotFound, error.Kind);
        Assert.Equal("app/db", error.Subject);
    }

    [Fact]
    public async Task Get_Kv2VersionWithNullData_IsSecretNotFoundBecauseItWasDeleted()
    {
        const string deleted = """{"data":{"data":null,"metadata":{"deletion_time":"2026-01-01T00:00:00Z","version":2}}}""";

        var result = await Get(ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.OK, deleted)), "app", "password");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.SecretNotFound, error.Kind);
        Assert.Contains("deleted", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Get_Status401Or403_IsAuthenticationFailedAndKeepsVaultsReason(HttpStatusCode status)
    {
        var handler = StubHttpMessageHandler.Json(status, """{"errors":["permission denied"]}""");

        var result = await Get(ProviderFor(handler), "app/db", "password");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("permission denied", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Get_OtherFailureStatuses_AreProviderUnavailableAndNameTheStatus(HttpStatusCode status)
    {
        var result = await Get(ProviderFor(StubHttpMessageHandler.Json(status, """{"errors":["boom"]}""")), "app", "password");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderUnavailable, error.Kind);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task Get_Redirects_AreReportedNotFollowedSoTheTokenNeverGoesToAnotherHost(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://other-node.example.com:8200/v1/secret/data/app");
            return Task.FromResult(response);
        });

        var result = await Get(ProviderFor(handler), "app", "password");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderUnavailable, error.Kind);
        Assert.Contains("other-node.example.com", error.Detail, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Get_NetworkFailure_IsProviderUnavailableNamingTheHost()
    {
        var handler = StubHttpMessageHandler.Throwing(new HttpRequestException("Connection refused"));

        var result = await Get(ProviderFor(handler), "app", "password");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderUnavailable, error.Kind);
        Assert.Contains("vault.example.com", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"data":"a string"}""")]
    [InlineData("""{"data":{"metadata":{}}}""")]
    public async Task Get_UnexpectedSuccessBody_IsProviderUnavailable(string body)
    {
        var result = await Get(ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.OK, body)), "app", "password");

        Assert.Equal(ErrorKind.ProviderUnavailable, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task Get_ErrorsNeverContainTheTokenOrTheSecretsInTheResponse()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":{"data":{"other":"hunter2-do-not-leak"}}}""");

        var missingField = await Get(ProviderFor(handler), "app", "password");
        var denied = await Get(ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, """{"errors":["denied"]}""")), "app", "password");

        foreach (var error in missingField.Errors.Concat(denied.Errors))
        {
            Assert.DoesNotContain(Token, error.Detail + error.Subject, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2-do-not-leak", error.Detail + error.Subject, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Get_CancelledWhileWaiting_Throws()
    {
        var handler = new StubHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Get(ProviderFor(handler), "app", "password", cts.Token));
    }

    [Fact]
    public void Options_DoNotPrintTheToken()
    {
        var options = new VaultOptions(Address, null, "secret", 2, new SecretValue(Token));

        Assert.DoesNotContain(Token, options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_ReleasesTheHttpClient()
    {
        var provider = ProviderFor(StubHttpMessageHandler.Json(HttpStatusCode.OK, Kv2Body));

        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await Get(provider, "app", "password"));
    }
}
