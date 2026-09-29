using EnvSync.Application.Abstractions;
using EnvSync.Application.Buffers;
using EnvSync.Domain;

namespace EnvSync.Infrastructure.Tests;

internal static class EmitterAssert
{
    public static EnvironmentVariableName Name(string text)
    {
        Assert.True(EnvironmentVariableName.TryCreate(text, out var name));
        return name;
    }

    /// <summary>Runs the emitter and returns whether it accepted the value plus everything it wrote.</summary>
    public static (bool Accepted, string Output) Emit(IShellEmitter emitter, string name, string value)
    {
        using var writer = new PooledCharBufferWriter(256);
        var accepted = emitter.TryWriteVariable(Name(name), value, writer);
        return (accepted, writer.WrittenSpan.ToString());
    }
}
