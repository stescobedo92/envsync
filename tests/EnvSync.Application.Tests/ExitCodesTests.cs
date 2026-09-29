using System.Collections.Immutable;
using EnvSync.Application;
using EnvSync.Domain;

namespace EnvSync.Application.Tests;

public sealed class ExitCodesTests
{
    [Theory]
    [InlineData(ErrorKind.ManifestInvalid, 10)]
    [InlineData(ErrorKind.ProviderUnknown, 10)]
    [InlineData(ErrorKind.ProviderMisconfigured, 10)]
    [InlineData(ErrorKind.FieldRequired, 10)]
    [InlineData(ErrorKind.UnsupportedValueForShell, 14)]
    [InlineData(ErrorKind.Internal, 13)]
    [InlineData(ErrorKind.SecretNotFound, 11)]
    [InlineData(ErrorKind.SecretEmpty, 11)]
    [InlineData(ErrorKind.FieldNotFound, 11)]
    [InlineData(ErrorKind.AuthenticationFailed, 12)]
    [InlineData(ErrorKind.Timeout, 12)]
    [InlineData(ErrorKind.ProviderUnavailable, 12)]
    [InlineData(ErrorKind.UnsupportedSecretType, 12)]
    [InlineData(ErrorKind.ProcessLaunchFailed, 126)]
    [InlineData(ErrorKind.ExecutableNotFound, 127)]
    public void For_ErrorKind_MapsToItsDocumentedCode(ErrorKind kind, int expected)
    {
        Assert.Equal(expected, ExitCodes.For(kind));
    }

    [Fact]
    public void For_NoErrors_IsSuccess()
    {
        Assert.Equal(ExitCodes.Success, ExitCodes.For(ImmutableArray<Error>.Empty));
    }

    [Fact]
    public void For_MixedErrors_ConfigurationOutranksProviderOutranksMissing()
    {
        var missing = new Error(ErrorKind.SecretNotFound, "A");
        var provider = new Error(ErrorKind.AuthenticationFailed, "B");
        var config = new Error(ErrorKind.ManifestInvalid, "C");

        Assert.Equal(ExitCodes.MissingRequired, ExitCodes.For([missing]));
        Assert.Equal(ExitCodes.ProviderFailure, ExitCodes.For([missing, provider]));
        Assert.Equal(ExitCodes.ManifestInvalid, ExitCodes.For([missing, provider, config]));
    }

    [Fact]
    public void KnownCodes_DoNotCollideWithTheUsualProcessCodes()
    {
        int[] ours =
        [
            ExitCodes.ManifestInvalid, ExitCodes.MissingRequired, ExitCodes.ProviderFailure,
            ExitCodes.InternalError, ExitCodes.UnsupportedValue, ExitCodes.UsageError,
        ];

        Assert.DoesNotContain(1, ours);
        Assert.DoesNotContain(2, ours);
        Assert.Equal(6, ours.Distinct().Count());
        Assert.Equal(64, ExitCodes.UsageError);
        Assert.Equal(14, ExitCodes.UnsupportedValue);
    }
}
