using System.Collections.Immutable;
using EnvSync.Domain;

namespace EnvSync.Domain.Tests;

public sealed class ProfileValidatorTests
{
    private static ProviderDefinition Provider(string alias, string type = "azure-keyvault") =>
        new(alias, type, new Dictionary<string, string>());

    private static VariableSpec Variable(string name, string from, string reference = "some-secret", bool required = true)
    {
        Assert.True(EnvironmentVariableName.TryCreate(name, out var variableName));
        Assert.True(SecretReference.TryParse(reference, out var secretReference));
        return new VariableSpec(variableName, from, secretReference, required);
    }

    private static Profile ProfileOf(
        IEnumerable<ProviderDefinition> providers,
        IEnumerable<VariableSpec> variables) =>
        new("dev", [.. providers], [.. variables]);

    [Fact]
    public void Validate_ConsistentProfile_HasNoErrors()
    {
        var profile = ProfileOf(
            [Provider("kv"), Provider("aws", "aws-secrets")],
            [Variable("DB_PASSWORD", "kv"), Variable("API_KEY", "aws")]);

        Assert.Empty(ProfileValidator.Validate(profile));
    }

    [Fact]
    public void Validate_VariableReferencingAnUndeclaredProvider_IsReported()
    {
        var profile = ProfileOf([Provider("kv")], [Variable("API_KEY", "missing-alias")]);

        var error = Assert.Single(ProfileValidator.Validate(profile));

        Assert.Equal(ErrorKind.ProviderUnknown, error.Kind);
        Assert.Equal("API_KEY", error.Subject);
        Assert.Contains("missing-alias", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DuplicateVariableNames_AreReported()
    {
        var profile = ProfileOf([Provider("kv")], [Variable("TOKEN", "kv"), Variable("TOKEN", "kv", "other")]);

        var error = Assert.Single(ProfileValidator.Validate(profile));

        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Equal("TOKEN", error.Subject);
    }

    [Fact]
    public void Validate_DuplicateProviderAliases_AreReported()
    {
        var profile = ProfileOf([Provider("kv"), Provider("kv", "aws-secrets")], [Variable("A", "kv")]);

        var error = Assert.Single(ProfileValidator.Validate(profile));

        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Equal("kv", error.Subject);
    }

    [Fact]
    public void Validate_SeveralProblems_AreAllReportedInOneGo()
    {
        var profile = ProfileOf(
            [Provider("kv"), Provider("kv")],
            [Variable("A", "nope"), Variable("B", "kv"), Variable("B", "kv", "another")]);

        var errors = ProfileValidator.Validate(profile);

        Assert.Equal(3, errors.Length);
        Assert.Contains(errors, e => e is { Kind: ErrorKind.ProviderUnknown, Subject: "A" });
        Assert.Contains(errors, e => e is { Kind: ErrorKind.ManifestInvalid, Subject: "B" });
        Assert.Contains(errors, e => e is { Kind: ErrorKind.ManifestInvalid, Subject: "kv" });
    }

    [Fact]
    public void Validate_ProfileWithoutVariables_IsAllowed()
    {
        var profile = ProfileOf([Provider("kv")], []);

        Assert.Empty(ProfileValidator.Validate(profile));
    }
}
