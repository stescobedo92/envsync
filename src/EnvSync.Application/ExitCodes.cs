using System.Collections.Immutable;
using EnvSync.Application.Resolution;
using EnvSync.Domain;

namespace EnvSync.Application;

/// <summary>
/// Process exit codes. envsync's own codes live in 10-13 so they do not collide with the codes
/// (0, 1, 2) that most programs already use; the child's code passes through untouched on <c>run</c>.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int ManifestInvalid = 10;
    public const int MissingRequired = 11;
    public const int ProviderFailure = 12;
    public const int InternalError = 13;

    /// <summary>The command was found but could not be started (shell convention).</summary>
    public const int LaunchFailed = 126;

    /// <summary>The command was not found (shell convention).</summary>
    public const int ExecutableNotFound = 127;

    /// <summary>Interrupted by the user (128 + SIGINT).</summary>
    public const int Cancelled = 130;

    public static int For(ErrorKind kind) => kind switch
    {
        ErrorKind.ManifestInvalid or ErrorKind.ProviderUnknown or ErrorKind.ProviderMisconfigured
            or ErrorKind.FieldRequired or ErrorKind.UnsupportedValueForShell => ManifestInvalid,
        ErrorKind.SecretNotFound or ErrorKind.FieldNotFound => MissingRequired,
        ErrorKind.AuthenticationFailed or ErrorKind.Timeout or ErrorKind.ProviderUnavailable
            or ErrorKind.UnsupportedSecretType => ProviderFailure,
        ErrorKind.ProcessLaunchFailed => LaunchFailed,
        ErrorKind.ExecutableNotFound => ExecutableNotFound,
        _ => InternalError,
    };

    /// <summary>The most significant code among <paramref name="errors"/>: configuration, then provider, then missing keys.</summary>
    public static int For(ImmutableArray<Error> errors)
    {
        var best = Success;
        var bestRank = 0;

        foreach (var error in errors)
        {
            var code = For(error.Kind);
            var rank = RankOf(code);
            if (rank > bestRank)
            {
                best = code;
                bestRank = rank;
            }
        }

        return best;
    }

    public static int For(ResolutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return For(result.Errors);
    }

    private static int RankOf(int code) => code switch
    {
        LaunchFailed or ExecutableNotFound => 5,
        InternalError => 4,
        ManifestInvalid => 3,
        ProviderFailure => 2,
        MissingRequired => 1,
        _ => 0,
    };
}
