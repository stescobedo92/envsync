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
/// When it did not, <see cref="FailureScript"/> is what to print instead, so that whoever evaluates the output fails too.
/// </summary>
public sealed class ExportResult : IDisposable
{
    private readonly PooledCharBufferWriter? _script;

    public ExportResult(
        ResolutionResult resolution,
        ImmutableArray<Error> emitErrors,
        PooledCharBufferWriter? script,
        string failureScript = "")
    {
        Resolution = resolution;
        EmitErrors = emitErrors.IsDefault ? [] : emitErrors;
        _script = script;
        IsSuccess = script is not null;
        FailureScript = failureScript;
    }

    public ResolutionResult Resolution { get; }

    /// <summary>Variables the requested shell cannot represent safely.</summary>
    public ImmutableArray<Error> EmitErrors { get; }

    public bool IsSuccess { get; }

    public ReadOnlyMemory<char> Script => _script is null ? default : _script.WrittenMemory;

    /// <summary>Holds no secret: only a statement that fails with the right exit code. Empty when the export succeeded.</summary>
    public string FailureScript { get; }

    public void Dispose() => _script?.Dispose();
}
