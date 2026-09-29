using System.Collections.Immutable;
using EnvSync.Application.Buffers;
using EnvSync.Application.Resolution;
using EnvSync.Domain;

namespace EnvSync.Application.Export;

/// <summary>Ask for a script that sets the profile's variables in a shell session.</summary>
public readonly record struct ExportEnvironmentQuery(Profile Profile, ResolveOptions Options, ShellKind Shell);

/// <summary>
/// The outcome of an export. When it succeeded, <see cref="Script"/> lives in a pooled buffer that is wiped on
/// <see cref="Dispose"/>, so the caller must dispose it as soon as the script has been written out.
/// </summary>
public sealed class ExportResult : IDisposable
{
    private readonly PooledCharBufferWriter? _script;

    public ExportResult(ResolutionResult resolution, ImmutableArray<Error> emitErrors, PooledCharBufferWriter? script)
    {
        Resolution = resolution;
        EmitErrors = emitErrors.IsDefault ? [] : emitErrors;
        _script = script;
        IsSuccess = script is not null;
    }

    public ResolutionResult Resolution { get; }

    /// <summary>Variables the requested shell cannot represent safely.</summary>
    public ImmutableArray<Error> EmitErrors { get; }

    public bool IsSuccess { get; }

    public ReadOnlyMemory<char> Script => _script is null ? default : _script.WrittenMemory;

    public void Dispose() => _script?.Dispose();
}
