using EnvSync.Domain;

namespace EnvSync.Application.Resolution;

public enum VariableStatus
{
    Resolved,

    /// <summary>An optional variable whose secret does not exist; it is simply left out.</summary>
    Skipped,

    /// <summary>A required variable whose secret (or field) does not exist.</summary>
    Missing,

    /// <summary>The variable could not be read for a reason other than "it does not exist".</summary>
    Failed,

    /// <summary>Offline mode: the provider is configured correctly but nothing was requested.</summary>
    Unverified,
}

/// <summary>What happened to one variable. <see cref="Error"/>'s subject is always the variable name.</summary>
public readonly record struct VariableOutcome(VariableSpec Spec, VariableStatus Status, SecretValue Value, Error Error);
