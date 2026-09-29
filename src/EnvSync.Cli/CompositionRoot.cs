using EnvSync.Application.Abstractions;
using EnvSync.Application.Export;
using EnvSync.Application.Providers;
using EnvSync.Application.Requirements;
using EnvSync.Application.Resolution;
using EnvSync.Application.Run;
using EnvSync.Infrastructure.Manifests;
using EnvSync.Infrastructure.Processes;
using EnvSync.Infrastructure.Shells;
using EnvSync.Providers.Aws;
using EnvSync.Providers.Azure;
using EnvSync.Providers.Vault;

namespace EnvSync.Cli;

/// <summary>The handlers a command needs, already wired together.</summary>
internal sealed record CliServices(
    IManifestLoader ManifestLoader,
    IQueryHandler<CheckRequirementsQuery, ResolutionResult> Check,
    IQueryHandler<ExportEnvironmentQuery, ExportResult> Export,
    ICommandHandler<RunProcessCommand, RunProcessResult> Run,
    IReadOnlyList<IShellEmitter> ShellEmitters);

/// <summary>Seams for tests: anything left null keeps its real implementation.</summary>
internal sealed class CompositionOverrides
{
    public IReadOnlyList<ISecretProviderFactory>? ProviderFactories { get; init; }

    public IProcessLauncher? ProcessLauncher { get; init; }

    public IReadOnlyList<IShellEmitter>? ShellEmitters { get; init; }
}

/// <summary>
/// The only place that knows every concrete type. The object graph is built by hand ("pure DI"): no container, no assembly
/// scanning, no reflection, so what runs is exactly what is written here and it stays trimming- and Native AOT-safe.
/// </summary>
internal static class CompositionRoot
{
    public static CliServices Build(CliEnvironment environment, CompositionOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        ISecretProviderFactory[] factories = overrides?.ProviderFactories is { } custom
            ? [.. custom]
            :
            [
                new AzureKeyVaultProviderFactory(),
                new AwsSecretsProviderFactory(environment.GetEnvironmentVariable),
                new VaultProviderFactory(environment.GetEnvironmentVariable),
            ];

        IShellEmitter[] emitters = overrides?.ShellEmitters is { } customEmitters
            ? [.. customEmitters]
            : [new BashEmitter(), new PowerShellEmitter(), new CmdEmitter()];

        var registry = new ProviderRegistry(factories);
        var resolver = new ResolveEnvironmentQueryHandler(registry);
        var launcher = overrides?.ProcessLauncher ?? new SystemProcessLauncher(ExecutableResolver.ForCurrentProcess());

        return new CliServices(
            new JsonManifestLoader(registry),
            new CheckRequirementsQueryHandler(resolver),
            new ExportEnvironmentQueryHandler(resolver, emitters),
            new RunProcessCommandHandler(resolver, launcher),
            emitters);
    }
}
