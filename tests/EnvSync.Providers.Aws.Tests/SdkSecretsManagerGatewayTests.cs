using Amazon;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using EnvSync.Providers.Aws;

namespace EnvSync.Providers.Aws.Tests;

public sealed class SdkSecretsManagerGatewayTests
{
    /// <summary>The real client type with its one operation overridden: no network, no credentials.</summary>
    private sealed class FakeClient : AmazonSecretsManagerClient
    {
        private readonly Func<GetSecretValueRequest, CancellationToken, Task<GetSecretValueResponse>> _respond;

        public FakeClient(Func<GetSecretValueRequest, CancellationToken, Task<GetSecretValueResponse>> respond)
            : base(new AnonymousAWSCredentials(), RegionEndpoint.USEast1) =>
            _respond = respond;

        public override Task<GetSecretValueResponse> GetSecretValueAsync(
            GetSecretValueRequest request, CancellationToken cancellationToken = default) =>
            _respond(request, cancellationToken);
    }

    [Fact]
    public async Task GetSecretString_ReturnsTheStringOfTheResponse()
    {
        using var gateway = new SdkSecretsManagerGateway(
            new FakeClient((_, _) => Task.FromResult(new GetSecretValueResponse { SecretString = "the-value" })));

        var text = await gateway.GetSecretStringAsync("prod/db", TestContext.Current.CancellationToken);

        Assert.Equal("the-value", text);
    }

    [Fact]
    public async Task GetSecretString_AsksForExactlyTheRequestedSecretId()
    {
        GetSecretValueRequest? seen = null;
        using var gateway = new SdkSecretsManagerGateway(new FakeClient((request, _) =>
        {
            seen = request;
            return Task.FromResult(new GetSecretValueResponse { SecretString = "v" });
        }));

        await gateway.GetSecretStringAsync("prod/db", TestContext.Current.CancellationToken);

        Assert.Equal("prod/db", seen?.SecretId);
    }

    [Fact]
    public async Task GetSecretString_ABinarySecret_ReturnsNull()
    {
        using var binary = new MemoryStream([1, 2, 3]);
        using var gateway = new SdkSecretsManagerGateway(
            new FakeClient((_, _) => Task.FromResult(new GetSecretValueResponse { SecretBinary = binary })));

        Assert.Null(await gateway.GetSecretStringAsync("prod/cert", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSecretString_SdkExceptionsFlowThroughForTheProviderToTranslate()
    {
        using var gateway = new SdkSecretsManagerGateway(
            new FakeClient((_, _) => throw new ResourceNotFoundException("missing")));

        await Assert.ThrowsAsync<ResourceNotFoundException>(async () =>
            await gateway.GetSecretStringAsync("prod/db", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSecretString_PassesTheCancellationTokenToTheSdk()
    {
        CancellationToken observed = default;
        using var gateway = new SdkSecretsManagerGateway(new FakeClient((_, token) =>
        {
            observed = token;
            return Task.FromResult(new GetSecretValueResponse { SecretString = "v" });
        }));
        using var cts = new CancellationTokenSource();

        await gateway.GetSecretStringAsync("prod/db", cts.Token);

        Assert.Equal(cts.Token, observed);
    }
}
