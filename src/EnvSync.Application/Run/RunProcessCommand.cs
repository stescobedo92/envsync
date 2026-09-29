using System.Collections.Immutable;
using EnvSync.Application.Resolution;
using EnvSync.Domain;

namespace EnvSync.Application.Run;

/// <summary>Launch <paramref name="Executable"/> with the profile's secrets injected into its environment only.</summary>
/// <param name="Resolved">
/// Called once, right after every required secret resolved and just before the child starts, so a caller can show warnings
/// (such as optional variables that were left unset) while they are still useful: the child may run for days.
/// It is not called when the preflight fails.
/// </param>
public readonly record struct RunProcessCommand(
    Profile Profile,
    ResolveOptions Options,
    string Executable,
    ImmutableArray<string> Arguments,
    Action<ResolutionResult>? Resolved = null);

/// <param name="Resolution">What was resolved, for reporting; always present.</param>
/// <param name="Launched">Whether the child was started at all.</param>
/// <param name="ExitCode">The child's exit code when launched; otherwise the code envsync should exit with.</param>
/// <param name="LaunchError">Why the child could not be started, if that is what went wrong.</param>
public sealed record RunProcessResult(ResolutionResult Resolution, bool Launched, int ExitCode, Error? LaunchError);
