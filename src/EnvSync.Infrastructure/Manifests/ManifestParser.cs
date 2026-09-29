using System.Collections.Immutable;
using System.Text.Json;
using EnvSync.Application.Providers;
using EnvSync.Domain;

namespace EnvSync.Infrastructure.Manifests;

/// <summary>
/// Turns the text of <c>envsync.json</c> into a <see cref="Manifest"/>. Comments and trailing commas are allowed.
/// It is strict on purpose (unknown properties are errors, so a typo like <c>"require"</c> cannot silently do nothing)
/// and it reports every problem at once, each with the JSON path it belongs to.
/// It walks a <see cref="JsonDocument"/> instead of binding to classes, so there is no reflection and no serializer metadata.
/// </summary>
public static class ManifestParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Result<Manifest> Parse(string json, string source, ProviderRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(source);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException exception)
        {
            return Result<Manifest>.Failure(new Error(ErrorKind.ManifestInvalid, source, Describe(exception)));
        }

        using (document)
        {
            return new Reader(registry).Read(document.RootElement, source);
        }
    }

    private static string Describe(JsonException exception)
    {
        // The framework message ends with "Path: ... | LineNumber: ... | BytePositionInLine: ..."; keep only the readable part.
        var message = exception.Message;
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0)
        {
            cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        }

        var reason = cut > 0 ? message[..cut] : message;
        var where = exception.LineNumber is { } line ? $" (line {line + 1})" : string.Empty;
        return $"The manifest is not valid JSON{where}: {reason}";
    }

    private sealed class Reader(ProviderRegistry? registry)
    {
        private readonly ImmutableArray<Error>.Builder _errors = ImmutableArray.CreateBuilder<Error>();

        public Result<Manifest> Read(JsonElement root, string source)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result<Manifest>.Failure(new Error(ErrorKind.ManifestInvalid, source, "The manifest must be a JSON object."));
            }

            string? defaultProfile = null;
            var profiles = ImmutableArray<Profile>.Empty;
            var sawProfiles = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    Duplicate(property.Name);
                    continue;
                }

                switch (property.Name)
                {
                    case "defaultProfile":
                        defaultProfile = ReadString(property.Value, "defaultProfile");
                        break;
                    case "profiles":
                        sawProfiles = true;
                        profiles = ReadProfiles(property.Value);
                        break;
                    default:
                        Unknown(string.Empty, property.Name, "defaultProfile, profiles");
                        break;
                }
            }

            if (!sawProfiles)
            {
                Invalid("profiles", "'profiles' is required and must declare at least one profile.");
            }

            return _errors.Count > 0
                ? Result<Manifest>.Failure(_errors.ToImmutable())
                : Result<Manifest>.Success(new Manifest(defaultProfile, profiles));
        }

        private ImmutableArray<Profile> ReadProfiles(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                Invalid("profiles", "'profiles' must be an object that maps profile names to profiles.");
                return [];
            }

            var profiles = ImmutableArray.CreateBuilder<Profile>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var declared = 0;

            foreach (var property in value.EnumerateObject())
            {
                declared++;
                var path = Join("profiles", property.Name);
                if (!seen.Add(property.Name))
                {
                    Duplicate(path);
                    continue;
                }

                if (ReadProfile(property.Name, property.Value, path) is { } profile)
                {
                    profiles.Add(profile);
                    _errors.AddRange(ProfileValidator.Validate(profile));
                }
            }

            if (declared == 0)
            {
                Invalid("profiles", "'profiles' must declare at least one profile.");
            }

            return profiles.ToImmutable();
        }

        private Profile? ReadProfile(string name, JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Invalid(path, "A profile must be an object with 'providers' and 'variables'.");
                return null;
            }

            var providers = ImmutableArray<ProviderDefinition>.Empty;
            var variables = ImmutableArray<VariableSpec>.Empty;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = Join(path, property.Name);
                if (!seen.Add(property.Name))
                {
                    Duplicate(propertyPath);
                    continue;
                }

                switch (property.Name)
                {
                    case "providers":
                        providers = ReadProviders(property.Value, propertyPath);
                        break;
                    case "variables":
                        variables = ReadVariables(property.Value, propertyPath);
                        break;
                    default:
                        Unknown(path, property.Name, "providers, variables");
                        break;
                }
            }

            return new Profile(name, providers, variables);
        }

        private ImmutableArray<ProviderDefinition> ReadProviders(JsonElement value, string path)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                Invalid(path, "'providers' must be an object that maps an alias to a provider.");
                return [];
            }

            var providers = ImmutableArray.CreateBuilder<ProviderDefinition>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in value.EnumerateObject())
            {
                var providerPath = Join(path, property.Name);
                if (!seen.Add(property.Name))
                {
                    Duplicate(providerPath);
                    continue;
                }

                if (ReadProvider(property.Name, property.Value, providerPath) is { } provider)
                {
                    providers.Add(provider);
                }
            }

            return providers.ToImmutable();
        }

        private ProviderDefinition? ReadProvider(string alias, JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Invalid(path, "A provider must be an object with a 'type' and its settings.");
                return null;
            }

            string? type = null;
            var sawType = false;
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in element.EnumerateObject())
            {
                var settingPath = Join(path, property.Name);
                if (!seen.Add(property.Name))
                {
                    Duplicate(settingPath);
                    continue;
                }

                if (property.Name == "type")
                {
                    sawType = true;
                    type = ReadString(property.Value, settingPath);
                }
                else if (TryReadScalar(property.Value, out var text))
                {
                    settings[property.Name] = text;
                }
                else
                {
                    Invalid(settingPath, "A provider setting must be a string, a number or a boolean.");
                }
            }

            var typePath = Join(path, "type");
            if (!sawType)
            {
                Invalid(typePath, "A provider needs a 'type', for example \"azure-keyvault\".");
                return null;
            }

            if (type is null)
            {
                return null;
            }

            if (type.Length == 0)
            {
                Invalid(typePath, "The provider 'type' must not be empty.");
                return null;
            }

            if (registry is not null && !registry.IsRegistered(type))
            {
                _errors.Add(new Error(
                    ErrorKind.ProviderUnknown,
                    typePath,
                    $"Provider type '{type}' is not supported. Registered types: {string.Join(", ", registry.RegisteredTypes)}."));
            }

            return new ProviderDefinition(alias, type, settings);
        }

        private ImmutableArray<VariableSpec> ReadVariables(JsonElement value, string path)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                Invalid(path, "'variables' must be an object that maps an environment variable name to where its secret lives.");
                return [];
            }

            var variables = ImmutableArray.CreateBuilder<VariableSpec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in value.EnumerateObject())
            {
                var variablePath = Join(path, property.Name);
                if (!seen.Add(property.Name))
                {
                    Duplicate(variablePath);
                    continue;
                }

                if (!EnvironmentVariableName.TryCreate(property.Name, out var name))
                {
                    Invalid(variablePath, $"'{property.Name}' is not a valid environment variable name (ASCII letters, digits and '_', not starting with a digit).");
                    continue;
                }

                if (ReadVariable(name, property.Value, variablePath) is { } variable)
                {
                    variables.Add(variable);
                }
            }

            return variables.ToImmutable();
        }

        private VariableSpec? ReadVariable(EnvironmentVariableName name, JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Invalid(path, "A variable must be an object like { \"from\": \"alias\", \"ref\": \"secret-name\" }.");
                return null;
            }

            string? from = null;
            SecretReference? reference = null;
            var required = true;
            var valid = true;
            var sawFrom = false;
            var sawRef = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = Join(path, property.Name);
                if (!seen.Add(property.Name))
                {
                    Duplicate(propertyPath);
                    valid = false;
                    continue;
                }

                switch (property.Name)
                {
                    case "from":
                        sawFrom = true;
                        from = ReadString(property.Value, propertyPath);
                        valid &= from is { Length: > 0 };
                        if (from is { Length: 0 })
                        {
                            Invalid(propertyPath, "'from' must name a provider alias.");
                        }

                        break;
                    case "ref":
                        sawRef = true;
                        reference = ReadReference(property.Value, propertyPath);
                        valid &= reference is not null;
                        break;
                    case "required":
                        if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            required = property.Value.GetBoolean();
                        }
                        else
                        {
                            Invalid(propertyPath, "'required' must be true or false.");
                            valid = false;
                        }

                        break;
                    default:
                        Unknown(path, property.Name, "from, ref, required");
                        break;
                }
            }

            if (!sawFrom)
            {
                Invalid(Join(path, "from"), "A variable needs 'from', the alias of the provider that holds it.");
                valid = false;
            }

            if (!sawRef)
            {
                Invalid(Join(path, "ref"), "A variable needs 'ref', the secret name (optionally 'name#field').");
                valid = false;
            }

            return valid && from is not null && reference is { } secret
                ? new VariableSpec(name, from, secret, required)
                : null;
        }

        private SecretReference? ReadReference(JsonElement value, string path)
        {
            if (ReadString(value, path) is not { } text)
            {
                return null;
            }

            if (SecretReference.TryParse(text, out var reference))
            {
                return reference;
            }

            Invalid(path, "'ref' must be a secret name or 'name#field', without leading or trailing spaces, control characters or an empty part.");
            return null;
        }

        private string? ReadString(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            Invalid(path, "This value must be a string.");
            return null;
        }

        private static bool TryReadScalar(JsonElement value, out string text)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    text = value.GetString() ?? string.Empty;
                    return true;
                case JsonValueKind.Number:
                    text = value.GetRawText();
                    return true;
                case JsonValueKind.True:
                    text = "true";
                    return true;
                case JsonValueKind.False:
                    text = "false";
                    return true;
                default:
                    text = string.Empty;
                    return false;
            }
        }

        private void Invalid(string subject, string detail) =>
            _errors.Add(new Error(ErrorKind.ManifestInvalid, subject, detail));

        private void Duplicate(string path) =>
            Invalid(path, "This key is declared more than once.");

        private void Unknown(string parentPath, string name, string allowed) =>
            Invalid(Join(parentPath, name), $"Unknown property '{name}'. Allowed here: {allowed}.");

        private static string Join(string parent, string name) => parent.Length == 0 ? name : $"{parent}.{name}";
    }
}
