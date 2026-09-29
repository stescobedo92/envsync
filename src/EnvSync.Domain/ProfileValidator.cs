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

        // Case-insensitive on every OS: Windows merges names that differ only by case, so a manifest must not
        // mean one thing there and another elsewhere.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            if (ReservedVariableNames.IsReserved(name))
            {
                errors.Add(new Error(
                    ErrorKind.ManifestInvalid,
                    name,
                    $"'{name}' is a reserved variable name: a secret must not be able to change where programs are loaded from or how a shell behaves."));
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
