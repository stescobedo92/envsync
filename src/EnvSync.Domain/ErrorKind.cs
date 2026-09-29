namespace EnvSync.Domain;

/// <summary>
/// Stable categories of <em>expected</em> failures. Provider adapters translate SDK exceptions into these
/// so nothing above them ever sees an Azure, AWS or HTTP type.
/// </summary>
public enum ErrorKind
{
    ManifestInvalid,
    ProviderUnknown,
    ProviderMisconfigured,
    AuthenticationFailed,
    SecretNotFound,

    /// <summary>The secret exists but its value is the empty string, which no program can be usefully given.</summary>
    SecretEmpty,
    FieldNotFound,
    FieldRequired,

    /// <summary>The secret exists but is not something that can become a variable: binary, invalid JSON, a nested object.</summary>
    UnsupportedSecretType,
    Timeout,
    ProviderUnavailable,
    UnsupportedValueForShell,
    ExecutableNotFound,
    ProcessLaunchFailed,

    /// <summary>A bug or an exception nobody anticipated: never to be mistaken for a transient outage.</summary>
    Internal,
}
