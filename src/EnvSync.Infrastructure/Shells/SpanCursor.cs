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

    public void Append(ReadOnlySpan<char> text)
    {
        text.CopyTo(_span[_position..]);
        _position += text.Length;
    }
}
