namespace EnvSync.Domain;

/// <summary>
/// A secret in memory. Every implicit rendering (<see cref="ToString"/>, interpolation, record printing)
/// is redacted, so a log line or an exception message cannot leak it by accident.
/// </summary>
public readonly struct SecretValue : IEquatable<SecretValue>
{
    private const string Redacted = "[REDACTED]";

    private readonly string? _value;

    public SecretValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    /// <summary>The plain text. Calling this is the explicit, greppable act of exposing the secret.</summary>
    public string Reveal() => _value ?? string.Empty;

    public override string ToString() => Redacted;

    public bool Equals(SecretValue other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SecretValue other && Equals(other);

    public override int GetHashCode() => _value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    public static bool operator ==(SecretValue left, SecretValue right) => left.Equals(right);

    public static bool operator !=(SecretValue left, SecretValue right) => !left.Equals(right);
}
