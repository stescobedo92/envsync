using EnvSync.Application.Secrets;
using EnvSync.Domain;

namespace EnvSync.Application.Tests;

public sealed class StructuredSecretTests
{
    private static SecretReference Ref(string path, string? field) => new(path, field);

    [Fact]
    public void Select_WithoutField_ReturnsThePayloadAsIs()
    {
        var result = StructuredSecret.Select("plain-token", Ref("api", null));

        Assert.True(result.IsSuccess);
        Assert.Equal("plain-token", result.Value.Reveal());
    }

    [Fact]
    public void Select_WithoutField_DoesNotTryToParseJson()
    {
        const string looksLikeJson = "{not really json";

        var result = StructuredSecret.Select(looksLikeJson, Ref("api", null));

        Assert.Equal(looksLikeJson, result.Value.Reveal());
    }

    [Fact]
    public void Select_StringField_ReturnsItsValue()
    {
        var result = StructuredSecret.Select("""{"user":"admin","password":"p@ss"}""", Ref("db", "password"));

        Assert.Equal("p@ss", result.Value.Reveal());
    }

    [Theory]
    [InlineData("""{"port":5432}""", "port", "5432")]
    [InlineData("""{"ratio":1.5e3}""", "ratio", "1.5e3")]
    [InlineData("""{"enabled":true}""", "enabled", "true")]
    [InlineData("""{"enabled":false}""", "enabled", "false")]
    public void Select_ScalarField_ReturnsItsLiteralText(string json, string field, string expected)
    {
        var result = StructuredSecret.Select(json, Ref("cfg", field));

        Assert.Equal(expected, result.Value.Reveal());
    }

    [Fact]
    public void Select_EscapedAndUnicodeText_IsDecoded()
    {
        var result = StructuredSecret.Select("""{"note":"line1\nline2 é \"q\""}""", Ref("cfg", "note"));

        Assert.Equal("line1\nline2 é \"q\"", result.Value.Reveal());
    }

    [Fact]
    public void Select_FieldMatchesEscapedPropertyNames()
    {
        var result = StructuredSecret.Select("""{"password":"x"}""", Ref("db", "password"));

        Assert.Equal("x", result.Value.Reveal());
    }

    [Fact]
    public void Select_MissingField_IsFieldNotFoundAndNamesTheField()
    {
        var result = StructuredSecret.Select("""{"user":"admin"}""", Ref("db", "password"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.FieldNotFound, error.Kind);
        Assert.Equal("db", error.Subject);
        Assert.Contains("password", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_FieldNamesAreCaseSensitive()
    {
        var result = StructuredSecret.Select("""{"Password":"x"}""", Ref("db", "password"));

        Assert.Equal(ErrorKind.FieldNotFound, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public void Select_OnlyTopLevelPropertiesCount()
    {
        var result = StructuredSecret.Select("""{"nested":{"password":"deep"}}""", Ref("db", "password"));

        Assert.Equal(ErrorKind.FieldNotFound, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public void Select_FieldFoundAfterSkippingNestedValues()
    {
        var result = StructuredSecret.Select("""{"meta":{"a":[1,2,{"b":3}]},"password":"ok"}""", Ref("db", "password"));

        Assert.Equal("ok", result.Value.Reveal());
    }

    [Theory]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":{"a":1}}""")]
    [InlineData("""{"value":[1,2]}""")]
    public void Select_NonScalarField_IsRejected(string json)
    {
        var result = StructuredSecret.Select(json, Ref("cfg", "value"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.FieldNotFound, error.Kind);
        Assert.Contains("scalar", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"broken": """)]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void Select_PayloadThatIsNotAJsonObject_IsFieldNotFound(string payload)
    {
        var result = StructuredSecret.Select(payload, Ref("cfg", "anything"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.FieldNotFound, error.Kind);
        Assert.Contains("JSON", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_ErrorsNeverEchoThePayload()
    {
        const string sensitive = "hunter2-do-not-leak";

        var result = StructuredSecret.Select($$"""{"other":"{{sensitive}}"}""", Ref("db", "password"));

        var error = Assert.Single(result.Errors);
        Assert.DoesNotContain(sensitive, error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, error.Subject, StringComparison.Ordinal);
    }
}
