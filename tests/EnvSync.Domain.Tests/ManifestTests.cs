using System.Collections.Immutable;
using EnvSync.Domain;

namespace EnvSync.Domain.Tests;

public sealed class ManifestTests
{
    private static Profile ProfileNamed(string name) => new(name, [], []);

    private static Manifest ManifestOf(string? defaultProfile, params string[] profiles) =>
        new(defaultProfile, [.. profiles.Select(ProfileNamed)]);

    [Fact]
    public void SelectProfile_ExplicitName_WinsOverTheDefault()
    {
        var manifest = ManifestOf("dev", "dev", "staging");

        var result = manifest.SelectProfile("staging");

        Assert.True(result.IsSuccess);
        Assert.Equal("staging", result.Value.Name);
    }

    [Fact]
    public void SelectProfile_NoNameButDefaultDeclared_UsesTheDefault()
    {
        var manifest = ManifestOf("staging", "dev", "staging");

        var result = manifest.SelectProfile(null);

        Assert.True(result.IsSuccess);
        Assert.Equal("staging", result.Value.Name);
    }

    [Fact]
    public void SelectProfile_NoNameAndASingleProfile_UsesIt()
    {
        var manifest = ManifestOf(null, "only");

        var result = manifest.SelectProfile(null);

        Assert.True(result.IsSuccess);
        Assert.Equal("only", result.Value.Name);
    }

    [Fact]
    public void SelectProfile_NoNameSeveralProfilesAndNoDefault_AsksTheUserToChoose()
    {
        var manifest = ManifestOf(null, "dev", "staging");

        var result = manifest.SelectProfile(null);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Contains("dev", error.Detail, StringComparison.Ordinal);
        Assert.Contains("staging", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectProfile_UnknownName_ListsTheAvailableProfiles()
    {
        var manifest = ManifestOf(null, "dev", "staging");

        var result = manifest.SelectProfile("prod");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Equal("prod", error.Subject);
        Assert.Contains("dev", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectProfile_DefaultPointingToAMissingProfile_Fails()
    {
        var manifest = ManifestOf("ghost", "dev");

        var result = manifest.SelectProfile(null);

        var error = Assert.Single(result.Errors);
        Assert.Equal("ghost", error.Subject);
    }

    [Fact]
    public void SelectProfile_ProfileNamesAreCaseSensitive()
    {
        var manifest = ManifestOf(null, "dev");

        Assert.False(manifest.SelectProfile("DEV").IsSuccess);
    }

    [Fact]
    public void SelectProfile_ManifestWithoutProfiles_Fails()
    {
        var manifest = new Manifest(null, ImmutableArray<Profile>.Empty);

        Assert.False(manifest.SelectProfile(null).IsSuccess);
    }
}
