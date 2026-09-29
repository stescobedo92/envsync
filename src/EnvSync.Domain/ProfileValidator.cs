using System.Collections.Immutable;

namespace EnvSync.Domain;

/// <summary>Checks that a profile is internally consistent, reporting every problem instead of stopping at the first.</summary>
public static class ProfileValidator
{
    public static ImmutableArray<Error> Validate(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = ImmutableArray.CreateBuilder<Error>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var provider in profile.Providers)
        {
            if (!aliases.Add(provider.Alias))
            {
                errors.Add(new Error(
                    ErrorKind.ManifestInvalid,
                    provider.Alias,
                    $"Provider alias '{provider.Alias}' is declared more than once."));
            }
        }

        foreach (var variable in profile.Variables)
        {
            var name = variable.Name.Value;

            if (!names.Add(name))
            {
                errors.Add(new Error(
                    ErrorKind.ManifestInvalid,
                    name,
                    $"Variable '{name}' is declared more than once."));
            }

            if (!aliases.Contains(variable.From))
            {
                errors.Add(new Error(
                    ErrorKind.ProviderUnknown,
                    name,
                    $"Variable '{name}' uses provider '{variable.From}', which profile '{profile.Name}' does not declare."));
            }
        }

        return errors.ToImmutable();
    }
}
