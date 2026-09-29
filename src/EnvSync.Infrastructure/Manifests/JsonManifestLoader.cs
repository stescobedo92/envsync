using EnvSync.Application.Abstractions;
using EnvSync.Application.Providers;
using EnvSync.Domain;

namespace EnvSync.Infrastructure.Manifests;

public sealed class JsonManifestLoader : IManifestLoader
{
    private readonly ProviderRegistry _registry;

    public JsonManifestLoader(ProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public async ValueTask<Result<Manifest>> LoadAsync(string? explicitPath, string startDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var path = string.IsNullOrEmpty(explicitPath)
            ? ManifestLocator.FindUpwards(startDirectory)
            : Path.GetFullPath(explicitPath, startDirectory);

        if (path is null)
        {
            return Fail(
                startDirectory,
                $"No {ManifestLocator.FileName} was found in '{startDirectory}' or any parent directory. Create one, or pass --manifest <path>.");
        }

        if (!File.Exists(path))
        {
            return Fail(path, $"The manifest file was not found: {path}");
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail(path, $"The manifest could not be read: {exception.Message}");
        }

        return ManifestParser.Parse(json, path, _registry);
    }

    private static Result<Manifest> Fail(string subject, string detail) =>
        Result<Manifest>.Failure(new Error(ErrorKind.ManifestInvalid, subject, detail));
}
