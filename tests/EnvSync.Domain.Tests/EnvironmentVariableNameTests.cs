using EnvSync.Domain;
using EnvSync.Testing;

namespace EnvSync.Domain.Tests;

public sealed class EnvironmentVariableNameTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("_")]
    [InlineData("_1")]
    [InlineData("a1")]
    [InlineData("DB_PASSWORD")]
    [InlineData("Azure__Client_Id")]
    public void IsValid_ValidNames_ReturnsTrue(string text)
    {
        Assert.True(EnvironmentVariableName.IsValid(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1A")]
    [InlineData("A-B")]
    [InlineData("A B")]
    [InlineData("A=B")]
    [InlineData("A.B")]
    [InlineData("ÁB")]
    [InlineData("A\0")]
    [InlineData("A\n")]
    public void IsValid_InvalidNames_ReturnsFalse(string text)
    {
        Assert.False(EnvironmentVariableName.IsValid(text));
    }

    [Fact]
    public void TryCreate_ValidName_ExposesTheValue()
    {
        var created = EnvironmentVariableName.TryCreate("DB_PASSWORD", out var name);

        Assert.True(created);
        Assert.Equal("DB_PASSWORD", name.Value);
        Assert.Equal("DB_PASSWORD", name.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9LIVES")]
    public void TryCreate_InvalidName_ReturnsFalse(string? text)
    {
        Assert.False(EnvironmentVariableName.TryCreate(text, out _));
    }

    [Fact]
    public void IsValid_DoesNotAllocate()
    {
        const string text = "AZURE_CLIENT_SECRET";

        var allocated = AllocationProbe.Measure(() => EnvironmentVariableName.IsValid(text));

        Assert.Equal(0, allocated);
    }
}
