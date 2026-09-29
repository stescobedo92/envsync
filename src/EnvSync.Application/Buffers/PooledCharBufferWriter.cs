using System.Buffers;

namespace EnvSync.Application.Buffers;

/// <summary>
/// A growable <see cref="IBufferWriter{T}"/> over pooled arrays. It exists so secrets can be rendered without
/// allocating per write, and because every array it lets go of is returned <em>cleared</em>: the one place
/// where a secret can actually be wiped from memory.
/// </summary>
public sealed class PooledCharBufferWriter : IBufferWriter<char>, IDisposable
{
    private readonly ArrayPool<char> _pool;
    private char[]? _buffer;
    private int _written;

    public PooledCharBufferWriter(int initialCapacity = 4096, ArrayPool<char>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);

        _pool = pool ?? ArrayPool<char>.Shared;
        _buffer = _pool.Rent(initialCapacity);
    }

    public int WrittenCount => _written;

    public ReadOnlySpan<char> WrittenSpan => Buffer.AsSpan(0, _written);

    public ReadOnlyMemory<char> WrittenMemory => Buffer.AsMemory(0, _written);

    private char[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PooledCharBufferWriter));

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Buffer.Length - _written);

        _written += count;
    }

    public Memory<char> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return Buffer.AsMemory(_written);
    }

    public Span<char> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return Buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            _pool.Return(buffer, clearArray: true);
        }

        _written = 0;
    }

    private void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        var current = Buffer;
        var needed = Math.Max(sizeHint, 1);
        if (current.Length - _written >= needed)
        {
            return;
        }

        var newSize = checked(Math.Max(current.Length * 2, _written + needed));
        var bigger = _pool.Rent(newSize);
        current.AsSpan(0, _written).CopyTo(bigger);
        _pool.Return(current, clearArray: true);
        _buffer = bigger;
    }
}
