namespace EnvSync.Domain;

/// <summary>
/// Where a secret lives inside a provider: a <see cref="Path"/> and, optionally, a <see cref="Field"/> to pick
/// from a structured secret. Written as <c>path</c> or <c>path#field</c>; the first <c>#</c> separates them.
/// </summary>
public readonly record struct SecretReference(string Path, string? Field)
{
    private const char FieldSeparator = '#';

    /// <summary>Allocation-free split. An empty <paramref name="field"/> means "no field".</summary>
    public static bool TrySplit(ReadOnlySpan<char> text, out ReadOnlySpan<char> path, out ReadOnlySpan<char> field)
    {
        var separator = text.IndexOf(FieldSeparator);
        if (separator < 0)
        {
            path = text;
            field = default;
            return IsWellFormed(path);
        }

        path = text[..separator];
        field = text[(separator + 1)..];
        return IsWellFormed(path) && IsWellFormed(field);
    }

    /// <summary>Parses a reference; allocates only the strings it keeps, and none at all when there is no field.</summary>
    public static bool TryParse(string? text, out SecretReference reference)
    {
        if (text is null || !TrySplit(text, out var path, out var field))
        {
            reference = default;
            return false;
        }

        var pathText = path.Length == text.Length ? text : new string(path);
        reference = new SecretReference(pathText, field.IsEmpty ? null : new string(field));
        return true;
    }

    private static bool IsWellFormed(ReadOnlySpan<char> part)
    {
        if (part.IsEmpty || char.IsWhiteSpace(part[0]) || char.IsWhiteSpace(part[^1]))
        {
            return false;
        }

        foreach (var c in part)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
