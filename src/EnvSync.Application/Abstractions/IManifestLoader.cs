using EnvSync.Domain;

namespace EnvSync.Application.Abstractions;

/// <summary>Finds and reads the manifest. It never touches a secret: the manifest holds only references.</summary>
public interface IManifestLoader
{
    /// <param name="explicitPath">A path from <c>--manifest</c>, relative to <paramref name="startDirectory"/>; when null the file is searched upwards.</param>
    /// <param name="startDirectory">Where to start looking, normally the current directory.</param>
    ValueTask<Result<Manifest>> LoadAsync(string? explicitPath, string startDirectory, CancellationToken cancellationToken);
}
