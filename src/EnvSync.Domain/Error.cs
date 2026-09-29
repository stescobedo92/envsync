namespace EnvSync.Domain;

/// <summary>An expected failure. <paramref name="Subject"/> is what it is about: a variable, a provider alias, a profile.</summary>
public readonly record struct Error(ErrorKind Kind, string Subject, string Detail = "");
