using EnvSync.Domain;

namespace EnvSync.Application.Resolution;

/// <summary>Resolve every variable of <paramref name="Profile"/> against its providers.</summary>
public readonly record struct ResolveEnvironmentQuery(
    Profile Profile,
    ResolveOptions Options,
    ResolveMode Mode = ResolveMode.Retain);
