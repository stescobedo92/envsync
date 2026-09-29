using EnvSync.Domain;

namespace EnvSync.TestSupport;

/// <summary>Terse builders so a test reads as the scenario it describes.</summary>
public static class TestProfiles
{
    public const string FakeType = "fake";

    public static ProviderDefinition Provider(string alias, string type = FakeType) =>
        new(alias, type, new Dictionary<string, string>());

    public static VariableSpec Variable(string name, string from, string reference, bool required = true)
    {
        if (!EnvironmentVariableName.TryCreate(name, out var variableName))
        {
            throw new InvalidOperationException($"'{name}' is not a valid variable name.");
        }

        if (!SecretReference.TryParse(reference, out var secretReference))
        {
            throw new InvalidOperationException($"'{reference}' is not a valid secret reference.");
        }

        return new VariableSpec(variableName, from, secretReference, required);
    }

    public static Profile Create(
        IEnumerable<ProviderDefinition> providers,
        IEnumerable<VariableSpec> variables,
        string name = "dev") =>
        new(name, [.. providers], [.. variables]);
}
