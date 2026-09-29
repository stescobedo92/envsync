using System.Collections.Immutable;

namespace EnvSync.Domain;

/// <summary>The parsed <c>envsync.json</c>: a set of profiles and, optionally, which one is the default.</summary>
public sealed record Manifest(string? DefaultProfile, ImmutableArray<Profile> Profiles)
{
    private const string ManifestSubject = "manifest";

    /// <summary>
    /// Picks the profile to use: the requested one, else <see cref="DefaultProfile"/>, else the only one.
    /// Names are case-sensitive, so the result never depends on the operating system.
    /// </summary>
    public Result<Profile> SelectProfile(string? requested)
    {
        if (!string.IsNullOrEmpty(requested))
        {
            return Find(requested, $"Profile '{requested}' does not exist");
        }

        if (DefaultProfile is not null)
        {
            return Find(DefaultProfile, $"defaultProfile '{DefaultProfile}' does not exist");
        }

        return Profiles.Length switch
        {
            1 => Result<Profile>.Success(Profiles[0]),
            0 => Failure(ManifestSubject, "The manifest declares no profiles."),
            _ => Failure(
                ManifestSubject,
                $"Several profiles are available ({AvailableNames()}); pass --profile or set defaultProfile."),
        };
    }

    private Result<Profile> Find(string name, string notFoundMessage)
    {
        foreach (var profile in Profiles)
        {
            if (string.Equals(profile.Name, name, StringComparison.Ordinal))
            {
                return Result<Profile>.Success(profile);
            }
        }

        return Failure(name, $"{notFoundMessage}. Available: {AvailableNames()}.");
    }

    private string AvailableNames() => string.Join(", ", Profiles.Select(static p => p.Name));

    private static Result<Profile> Failure(string subject, string detail) =>
        Result<Profile>.Failure(new Error(ErrorKind.ManifestInvalid, subject, detail));
}
