using System.Buffers;
using EnvSync.Application.Buffers;
using EnvSync.Testing;

namespace EnvSync.Application.Tests;

public sealed class PooledCharBufferWriterTests
{
    private sealed class RecordingPool : ArrayPool<char>
    {
        public List<(char[] Array, bool Cleared)> Returned { get; } = [];

        public override char[] Rent(int minimumLength) => new char[minimumLength];

        public override void Return(char[] array, bool clearArray = false) => Returned.Add((array, clearArray));
    }

    private static void Write(PooledCharBufferWriter writer, string text)
    {
        var span = writer.GetSpan(text.Length);
        text.CopyTo(span);
        writer.Advance(text.Length);
    }

    [Fact]
    public void Write_WithinCapacity_ExposesTheWrittenText()
    {
        using var writer = new PooledCharBufferWriter(64);

        Write(writer, "export A='1'\n");

        Assert.Equal("export A='1'\n", writer.WrittenSpan.ToString());
        Assert.Equal(13, writer.WrittenCount);
    }

    [Fact]
    public void Write_BeyondCapacity_GrowsAndKeepsEverythingWritten()
    {
        using var writer = new PooledCharBufferWriter(4);

        Write(writer, "abc");
        Write(writer, "defghijklmnop");

        Assert.Equal("abcdefghijklmnop", writer.WrittenSpan.ToString());
    }

    [Fact]
    public void Growing_ClearsTheBufferItReplaces()
    {
        var pool = new RecordingPool();
        using var writer = new PooledCharBufferWriter(4, pool);

        Write(writer, "abc");
        Write(writer, "defghijklmnop");

        var replaced = Assert.Single(pool.Returned);
        Assert.True(replaced.Cleared);
    }

    [Fact]
    public void Dispose_ReturnsTheBufferClearedToThePool()
    {
        var pool = new RecordingPool();
        var writer = new PooledCharBufferWriter(16, pool);
        Write(writer, "secret");

        writer.Dispose();

        var returned = Assert.Single(pool.Returned);
        Assert.True(returned.Cleared);
    }

    [Fact]
    public void Dispose_CalledTwice_ReturnsTheBufferOnlyOnce()
    {
        var pool = new RecordingPool();
        var writer = new PooledCharBufferWriter(16, pool);

        writer.Dispose();
        writer.Dispose();

        Assert.Single(pool.Returned);
    }

    [Fact]
    public void GetSpan_AfterDispose_Throws()
    {
        var writer = new PooledCharBufferWriter(16);
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(1));
    }

    [Fact]
    public void Advance_MoreThanWasReserved_Throws()
    {
        // An exact-size pool makes the capacity deterministic; the shared pool hands out at least 16 chars.
        using var writer = new PooledCharBufferWriter(8, new RecordingPool());

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(9));
    }

    [Fact]
    public void GetSpan_ZeroHint_StillReturnsANonEmptySpan()
    {
        using var writer = new PooledCharBufferWriter(8);

        Assert.False(writer.GetSpan(0).IsEmpty);
    }

    [Fact]
    public void Write_WithinCapacity_DoesNotAllocate()
    {
        using var writer = new PooledCharBufferWriter(4096);
        const string line = "export DB_PASSWORD='value'\n";

        var allocated = AllocationProbe.Measure(() =>
        {
            var span = writer.GetSpan(line.Length);
            line.CopyTo(span);
            writer.Advance(line.Length);
        });

        Assert.Equal(0, allocated);
    }
}
