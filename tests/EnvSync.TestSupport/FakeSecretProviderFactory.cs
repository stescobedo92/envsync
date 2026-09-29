using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.TestSupport;

/// <summary>Factory double that records which provider aliases it was asked to build.</summary>
public sealed class FakeSecretProviderFactory : ISecretProviderFactory
{
    private readonly Func<ProviderDefinition, Result<ISecretProvider>> _create;
    private readonly List<string> _createdAliases = [];
    private readonly Lock _gate = new();

    public FakeSecretProviderFactory(string type, Func<ProviderDefinition, Result<ISecretProvider>> create)
    {
        Type = type;
        _create = create;
    }

    public string Type { get; }

    public int CreateCalls
    {
        get
        {
            lock (_gate)
            {
                return _createdAliases.Count;
            }
        }
    }

    public static FakeSecretProviderFactory Returning(string type, ISecretProvider provider) =>
        new(type, _ => Result<ISecretProvider>.Success(provider));

    public static FakeSecretProviderFactory Failing(string type, ErrorKind kind, string detail = "") =>
        new(type, definition => Result<ISecretProvider>.Failure(new Error(kind, definition.Alias, detail)));

    public Result<ISecretProvider> Create(ProviderDefinition definition)
    {
        lock (_gate)
        {
            _createdAliases.Add(definition.Alias);
        }

        return _create(definition);
    }
}
