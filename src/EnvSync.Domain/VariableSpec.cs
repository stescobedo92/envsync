namespace EnvSync.Domain;

/// <summary>One environment variable to provide: its name, the provider alias it comes from, and where in that provider.</summary>
public readonly record struct VariableSpec(
    EnvironmentVariableName Name,
    string From,
    SecretReference Reference,
    bool Required = true);
