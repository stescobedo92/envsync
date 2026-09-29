using Amazon.Runtime;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;
using EnvSync.Providers.Aws;

namespace EnvSync.Providers.Aws.Tests;

public sealed class AwsSecretsProviderFactoryTests
{
    private sealed record Built(AWSCredentials? Credentials, string Region);

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
        new("aws", "aws-secrets", settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase));

    private static (AwsSecretsProviderFactory Factory, List<Built> Built) Recording(
        Environment? environment = null,
        Func<string, AWSCredentials?>? profiles = null)
    {
        var built = new List<Built>();
        var factory = new AwsSecretsProviderFactory(
            (environment ?? new Environment()).Get,
            profiles ?? (_ => null),
            (credentials, region) =>
            {
                built.Add(new Built(credentials, region));
                return FakeGateway.Returning("v");
            });

        return (factory, built);
    }

    [Fact]
    public void Type_IsAwsSecrets()
    {
        Assert.Equal("aws-secrets", new AwsSecretsProviderFactory().Type);
    }

    [Theory]
    [InlineData("us-east-1")]
    [InlineData("eu-central-2")]
    [InlineData("ap-southeast-2")]
    [InlineData("us-gov-west-1")]
    [InlineData("cn-north-1")]
    [InlineData("il-central-1")]
    public void Create_ValidRegion_BuildsTheGatewayForIt(string region)
    {
        var (factory, built) = Recording();

        var result = factory.Create(Definition(("region", region)));

        Assert.True(result.IsSuccess);
        var single = Assert.Single(built);
        Assert.Equal(region, single.Region);
        Assert.Null(single.Credentials);
    }

    [Theory]
    [InlineData("")]
    [InlineData("us_east_1")]
    [InlineData("US-EAST-1")]
    [InlineData("useast1")]
    [InlineData("us-east")]
    [InlineData("mars")]
    [InlineData("us-east-1; drop")]
    public void Create_InvalidRegion_IsMisconfigured(string region)
    {
        var (factory, built) = Recording();

        var result = factory.Create(Definition(("region", region)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Equal("aws", error.Subject);
        Assert.Empty(built);
    }

    [Fact]
    public void Create_MissingRegion_IsMisconfiguredAndMentionsTheWaysToSetIt()
    {
        var (factory, _) = Recording();

        var error = Assert.Single(factory.Create(Definition()).Errors);

        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("region", error.Detail, StringComparison.Ordinal);
        Assert.Contains("AWS_REGION", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AWS_REGION", "eu-west-1")]
    [InlineData("AWS_DEFAULT_REGION", "eu-west-2")]
    public void Create_RegionFallsBackToTheEnvironment(string variable, string region)
    {
        var (factory, built) = Recording(new Environment().With(variable, region));

        Assert.True(factory.Create(Definition()).IsSuccess);
        Assert.Equal(region, Assert.Single(built).Region);
    }

    [Fact]
    public void Create_AwsRegionBeatsAwsDefaultRegion_AndTheSettingBeatsBoth()
    {
        var environment = new Environment().With("AWS_REGION", "eu-west-1").With("AWS_DEFAULT_REGION", "eu-west-2");

        var (fromEnvironment, built) = Recording(environment);
        fromEnvironment.Create(Definition());
        var (fromSetting, builtBySetting) = Recording(environment);
        fromSetting.Create(Definition(("region", "us-east-2")));

        Assert.Equal("eu-west-1", Assert.Single(built).Region);
        Assert.Equal("us-east-2", Assert.Single(builtBySetting).Region);
    }

    [Fact]
    public void Create_WithAProfile_PassesItsCredentialsToTheGateway()
    {
        var credentials = new AnonymousAWSCredentials();
        var asked = new List<string>();
        var (factory, built) = Recording(profiles: name =>
        {
            asked.Add(name);
            return credentials;
        });

        var result = factory.Create(Definition(("region", "us-east-1"), ("profile", "work")));

        Assert.True(result.IsSuccess);
        Assert.Equal("work", Assert.Single(asked));
        Assert.Same(credentials, Assert.Single(built).Credentials);
    }

    [Fact]
    public void Create_AProfileThatDoesNotExist_IsMisconfiguredAndNamesIt()
    {
        var (factory, built) = Recording(profiles: _ => null);

        var error = Assert.Single(factory.Create(Definition(("region", "us-east-1"), ("profile", "ghost"))).Errors);

        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("ghost", error.Detail, StringComparison.Ordinal);
        Assert.Empty(built);
    }

    [Fact]
    public void Create_WithoutAProfile_LeavesCredentialsToTheSdkDefaultChain()
    {
        var asked = new List<string>();
        var (factory, built) = Recording(profiles: name =>
        {
            asked.Add(name);
            return null;
        });

        factory.Create(Definition(("region", "us-east-1")));

        Assert.Empty(asked);
        Assert.Null(Assert.Single(built).Credentials);
    }

    [Fact]
    public void Create_UnknownSetting_IsMisconfiguredSoTyposAreCaught()
    {
        var (factory, _) = Recording();

        var error = Assert.Single(factory.Create(Definition(("region", "us-east-1"), ("regoin", "typo"))).Errors);

        Assert.Equal(ErrorKind.ProviderMisconfigured, error.Kind);
        Assert.Contains("regoin", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ReturnsAProviderBackedByTheGateway()
    {
        var (factory, _) = Recording();

        var result = factory.Create(Definition(("region", "us-east-1")));

        Assert.IsType<AwsSecretsProvider>(result.Value);
    }

    [Fact]
    public void Create_WithTheDefaultGatewayFactory_BuildsWithoutTouchingTheNetwork()
    {
        var result = new AwsSecretsProviderFactory(_ => null, _ => null).Create(Definition(("region", "us-east-1")));

        Assert.True(result.IsSuccess);
        Assert.IsAssignableFrom<ISecretProvider>(result.Value);
        ((IDisposable)result.Value).Dispose();
    }
}
