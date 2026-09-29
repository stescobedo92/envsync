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
    FieldNotFound,
    FieldRequired,
    UnsupportedSecretType,
    Timeout,
    ProviderUnavailable,
    UnsupportedValueForShell,
    ExecutableNotFound,
    ProcessLaunchFailed,
}
