namespace EnvSync.Application.Resolution;

public enum ResolveMode
{
    /// <summary>Fetch every secret and keep its value (needed to build an environment).</summary>
    Retain,

    /// <summary>Fetch every secret to prove it exists, then drop the value immediately.</summary>
    Verify,

    /// <summary>Only build the providers, which validates their settings; no network request is made.</summary>
    Offline,
}
