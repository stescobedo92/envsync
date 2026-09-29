using System.Net;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using EnvSync.Domain;
using EnvSync.Providers.Aws;

namespace EnvSync.Providers.Aws.Tests;

public sealed class AwsSecretsProviderTests
{
    private static ValueTask<Result<SecretValue>> Get(FakeGateway gateway, string path, string? field = null, CancellationToken? token = null) =>
        new AwsSecretsProvider(gateway).GetAsync(new SecretReference(path, field), token ?? TestContext.Current.CancellationToken);

    private static AmazonSecretsManagerException Service(string errorCode, HttpStatusCode status, string message = "service message") =>
        new(message, ErrorType.Sender, errorCode, "request-id", status);

    [Fact]
    public async Task Get_ReturnsTheSecretStringAsIs()
    {
        var gateway = FakeGateway.Returning("p@ss w0rd");

        var result = await Get(gateway, "prod/db-password");

        Assert.True(result.IsSuccess);
        Assert.Equal("p@ss w0rd", result.Value.Reveal());
        Assert.Equal("prod/db-password", Assert.Single(gateway.Requested));
    }

    [Fact]
    public async Task Get_WithAField_PicksItFromTheJsonSecret()
    {
        var gateway = FakeGateway.Returning("""{"username":"admin","password":"s3cret"}""");

        var result = await Get(gateway, "prod/db", "password");

        Assert.Equal("s3cret", result.Value.Reveal());
    }

    [Fact]
    public async Task Get_WithAFieldTheSecretLacks_IsFieldNotFound()
    {
        var result = await Get(FakeGateway.Returning("""{"username":"admin"}"""), "prod/db", "password");

        Assert.Equal(ErrorKind.FieldNotFound, Assert.Single(result.Errors).Kind);
    }

    [Theory]
    [InlineData("prod/db-password")]
    [InlineData("my_secret+=.@-name")]
    [InlineData("arn:aws:secretsmanager:us-east-1:123456789012:secret:prod/db-AbCdEf")]
    public async Task Get_ValidSecretIds_ReachAws(string secretId)
    {
        var gateway = FakeGateway.Returning("v");

        Assert.True((await Get(gateway, secretId)).IsSuccess);
        Assert.Single(gateway.Requested);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("ünïcode")]
    [InlineData("star*")]
    public async Task Get_InvalidSecretIds_AreRejectedBeforeCallingAws(string secretId)
    {
        var gateway = FakeGateway.Returning("v");

        var error = Assert.Single((await Get(gateway, secretId)).Errors);

        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Equal(secretId, error.Subject);
        Assert.Empty(gateway.Requested);
    }

    [Fact]
    public async Task Get_TheLongestValidSecretIdHas512Characters()
    {
        var gateway = FakeGateway.Returning("v");

        Assert.True((await Get(gateway, new string('a', 512))).IsSuccess);
        Assert.False((await Get(gateway, new string('a', 513))).IsSuccess);
    }

    [Fact]
    public async Task Get_ABinarySecret_IsUnsupportedAndSaysHowToFixIt()
    {
        var error = Assert.Single((await Get(FakeGateway.Returning(secretString: null), "prod/cert")).Errors);

        Assert.Equal(ErrorKind.UnsupportedSecretType, error.Kind);
        Assert.Contains("text", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_ResourceNotFound_IsSecretNotFound()
    {
        var gateway = FakeGateway.Throwing(new ResourceNotFoundException("Secrets Manager can't find the specified secret."));

        var error = Assert.Single((await Get(gateway, "prod/db")).Errors);

        Assert.Equal(ErrorKind.SecretNotFound, error.Kind);
        Assert.Equal("prod/db", error.Subject);
    }

    [Fact]
    public async Task Get_ASecretScheduledForDeletion_IsSecretNotFound()
    {
        var gateway = FakeGateway.Throwing(new InvalidRequestException("You can't perform this operation on the secret because it was marked for deletion."));

        Assert.Equal(ErrorKind.SecretNotFound, Assert.Single((await Get(gateway, "prod/db")).Errors).Kind);
    }

    [Fact]
    public async Task Get_InvalidParameter_IsAManifestError()
    {
        var gateway = FakeGateway.Throwing(new InvalidParameterException("Invalid secret id."));

        Assert.Equal(ErrorKind.ManifestInvalid, Assert.Single((await Get(gateway, "prod/db")).Errors).Kind);
    }

    [Fact]
    public async Task Get_DecryptionFailure_IsAuthenticationFailedWithTheKmsHint()
    {
        var gateway = FakeGateway.Throwing(new DecryptionFailureException("Access to KMS is not allowed."));

        var error = Assert.Single((await Get(gateway, "prod/db")).Errors);

        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("KMS", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AccessDeniedException", HttpStatusCode.BadRequest)]
    [InlineData("UnrecognizedClientException", HttpStatusCode.BadRequest)]
    [InlineData("ExpiredTokenException", HttpStatusCode.BadRequest)]
    [InlineData("InvalidSignatureException", HttpStatusCode.Forbidden)]
    [InlineData("SomethingElse", HttpStatusCode.Forbidden)]
    [InlineData("SomethingElse", HttpStatusCode.Unauthorized)]
    public async Task Get_PermissionAndCredentialProblems_AreAuthenticationFailed(string code, HttpStatusCode status)
    {
        var gateway = FakeGateway.Throwing(Service(code, status, "User is not authorized to perform: secretsmanager:GetSecretValue"));

        var error = Assert.Single((await Get(gateway, "prod/db")).Errors);

        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("not authorized", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ThrottlingException", HttpStatusCode.BadRequest)]
    [InlineData("InternalServiceError", HttpStatusCode.InternalServerError)]
    [InlineData("ServiceUnavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("SomethingElse", HttpStatusCode.TooManyRequests)]
    public async Task Get_ThrottlingAndServerErrors_AreProviderUnavailable(string code, HttpStatusCode status)
    {
        var gateway = FakeGateway.Throwing(Service(code, status));

        var error = Assert.Single((await Get(gateway, "prod/db")).Errors);

        Assert.Equal(ErrorKind.ProviderUnavailable, error.Kind);
        Assert.Contains(code, error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_NoCredentialsFound_IsAuthenticationFailedWithTheConfigurationHint()
    {
        var gateway = FakeGateway.Throwing(new AmazonClientException("Unable to find credentials"));

        var error = Assert.Single((await Get(gateway, "prod/db")).Errors);

        Assert.Equal(ErrorKind.AuthenticationFailed, error.Kind);
        Assert.Contains("aws configure", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ANetworkLevelClientFailure_IsProviderUnavailable()
    {
        var gateway = FakeGateway.Throwing(new AmazonClientException("Connection reset by peer"));

        var error = Assert.Single((await Get(gateway, "prod/db")).Errors);

        Assert.Equal(ErrorKind.ProviderUnavailable, error.Kind);
        Assert.Contains("Connection reset", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_AMultiLineSdkMessage_IsKeptWholeOnOneLine()
    {
        var gateway = FakeGateway.Throwing(Service("AccessDeniedException", HttpStatusCode.BadRequest, "denied\r\nsecond line with request details"));

        var error = Assert.Single((await Get(gateway, "prod/db")).Errors);

        Assert.Contains("second line with request details", error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', error.Detail);
        Assert.DoesNotContain('\r', error.Detail);
    }

    [Fact]
    public async Task Get_AHugeSdkMessage_IsBounded()
    {
        var gateway = FakeGateway.Throwing(Service("AccessDeniedException", HttpStatusCode.BadRequest, new string('x', 20_000)));

        var error = Assert.Single((await Get(gateway, "prod/db")).Errors);

        Assert.True(error.Detail.Length < 1000, $"detail was {error.Detail.Length} characters");
    }

    [Fact]
    public async Task Get_Cancelled_Throws()
    {
        var gateway = new FakeGateway(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "never";
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Get(gateway, "prod/db", token: cts.Token));
    }

    [Fact]
    public void Dispose_ReleasesTheGateway()
    {
        var gateway = FakeGateway.Returning("v");

        new AwsSecretsProvider(gateway).Dispose();

        Assert.True(gateway.IsDisposed);
    }
}
