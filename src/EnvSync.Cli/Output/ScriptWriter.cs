using System.Buffers;
using System.Text;

namespace EnvSync.Cli.Output;

/// <summary>
/// Writes a script to stdout as UTF-8 without a BOM. The bytes go through a pooled buffer that is wiped before it is
/// returned, and the write goes straight to the stream: a <see cref="TextWriter"/> would keep its own copy of the secrets.
/// </summary>
internal static class ScriptWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAsync(Stream output, ReadOnlyMemory<char> script, CancellationToken cancellationToken)
    {
        var length = Utf8.GetByteCount(script.Span);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));

        try
        {
            var written = Utf8.GetBytes(script.Span, buffer);
            await output.WriteAsync(buffer.AsMemory(0, written), cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
