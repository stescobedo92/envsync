using EnvSync.Domain;
using EnvSync.Testing;

namespace EnvSync.Domain.Tests;

public sealed class SecretReferenceTests
{
    [Theory]
    [InlineData("db-password", "db-password", null)]
    [InlineData("prod/api-key", "prod/api-key", null)]
    [InlineData("app/jwt#signing", "app/jwt", "signing")]
    [InlineData("a#b#c", "a", "b#c")]
    public void TryParse_ValidReferences_SplitOnTheFirstHash(string text, string path, string? field)
    {
        var parsed = SecretReference.TryParse(text, out var reference);

        Assert.True(parsed);
        Assert.Equal(path, reference.Path);
        Assert.Equal(field, reference.Field);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#field")]
    [InlineData("path#")]
    [InlineData(" path")]
    [InlineData("path ")]
    [InlineData("pa\nth")]
    [InlineData("path#fi\teld")]
    public void TryParse_InvalidReferences_ReturnsFalse(string? text)
    {
        Assert.False(SecretReference.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_WithoutField_ReusesTheInputStringForThePath()
    {
        var text = new string("plain-secret-name".AsSpan());

        var parsed = SecretReference.TryParse(text, out var reference);

        Assert.True(parsed);
        Assert.Same(text, reference.Path);
    }

    [Fact]
    public void TrySplit_ReturnsSlicesOfTheInput()
    {
        var split = SecretReference.TrySplit("secret/app#jwt", out var path, out var field);

        Assert.True(split);
        Assert.Equal("secret/app", path.ToString());
        Assert.Equal("jwt", field.ToString());
    }

    [Fact]
    public void TrySplit_DoesNotAllocate()
    {
        const string text = "prod/database/credentials#password";

        var allocated = AllocationProbe.Measure(() => SecretReference.TrySplit(text, out _, out _));

        Assert.Equal(0, allocated);
    }
}
