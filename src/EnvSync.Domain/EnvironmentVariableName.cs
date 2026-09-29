namespace EnvSync.Domain;

/// <summary>A portable environment variable name: ASCII letters, digits and underscore, not starting with a digit.</summary>
public readonly record struct EnvironmentVariableName
{
    private readonly string? _value;

    private EnvironmentVariableName(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    /// <summary>Allocation-free validation over any text source.</summary>
    public static bool IsValid(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || !IsNameStart(text[0]))
        {
            return false;
        }

        foreach (var c in text[1..])
        {
            if (!IsNamePart(c))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryCreate(string? text, out EnvironmentVariableName name)
    {
        if (text is not null && IsValid(text))
        {
            name = new EnvironmentVariableName(text);
            return true;
        }

        name = default;
        return false;
    }

    public override string ToString() => Value;

    private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsNamePart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
