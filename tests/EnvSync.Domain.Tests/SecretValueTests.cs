using EnvSync.Domain;

namespace EnvSync.Domain.Tests;

public sealed class SecretValueTests
{
    private const string Plain = "s3cr3t-value";

    private sealed record Holder(SecretValue Value);

    [Fact]
    public void Reveal_ReturnsTheOriginalValue()
    {
        var secret = new SecretValue(Plain);

        Assert.Equal(Plain, secret.Reveal());
    }

    [Fact]
    public void ToString_IsRedacted()
    {
        var secret = new SecretValue(Plain);

        Assert.Equal("[REDACTED]", secret.ToString());
    }

    [Fact]
    public void StringInterpolation_DoesNotLeakTheValue()
    {
        var secret = new SecretValue(Plain);

        var rendered = $"value={secret}";

        Assert.DoesNotContain(Plain, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordContainingASecret_DoesNotLeakItThroughToString()
    {
        var holder = new Holder(new SecretValue(Plain));

        Assert.DoesNotContain(Plain, holder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultInstance_RevealsAnEmptyString()
    {
        Assert.Equal(string.Empty, default(SecretValue).Reveal());
    }

    [Fact]
    public void Equals_SameContent_IsTrue()
    {
        Assert.Equal(new SecretValue(Plain), new SecretValue(Plain));
    }

    [Fact]
    public void Equals_DifferentContent_IsFalse()
    {
        Assert.NotEqual(new SecretValue(Plain), new SecretValue("other"));
    }

    [Fact]
    public void Constructor_NullValue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SecretValue(null!));
    }
}
