using System.Globalization;

namespace EnvSync.Infrastructure.Shells;

/// <summary>
/// Appends to a span whose capacity the caller already reserved exactly, so emitters render
/// straight into the output buffer without an intermediate string.
/// </summary>
internal ref struct SpanCursor
{
    private readonly Span<char> _span;
    private int _position;

    public SpanCursor(Span<char> span)
    {
        _span = span;
        _position = 0;
    }

    public readonly int Length => _position;

    public void Append(char value) => _span[_position++] = value;

    /// <summary>Appends the decimal code of a character, so a non-printable one can be written as text.</summary>
    public void AppendCode(char value)
    {
        ((int)value).TryFormat(_span[_position..], out var written, default, CultureInfo.InvariantCulture);
        _position += written;
    }

    public void Append(ReadOnlySpan<char> text)
    {
        text.CopyTo(_span[_position..]);
        _position += text.Length;
    }
}
