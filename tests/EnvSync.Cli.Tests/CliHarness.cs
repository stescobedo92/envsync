using System.Text;
using EnvSync.Application.Abstractions;
using EnvSync.Cli;
using EnvSync.Domain;
using EnvSync.TestSupport;

namespace EnvSync.Cli.Tests;

/// <summary>A stdout that fails on every write, like a pipe whose reader has gone away (<c>envsync env | head -c0</c>).</summary>
internal sealed class ClosedPipeStream : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new IOException("The pipe is being closed.");

    public override Task FlushAsync(CancellationToken cancellationToken) => throw new IOException("The pipe is being closed.");

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new IOException("The pipe is being closed.");

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw new IOException("The pipe is being closed.");
}

/// <summary>A launcher that blows up, to prove an unexpected crash is reported instead of escaping.</summary>
internal sealed class CrashingLauncher : IProcessLauncher
{
    public ValueTask<Result<int>> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("kaboom");
}

internal sealed record CliResult(int ExitCode, byte[] StdoutBytes, string Stderr)
{
    public string Stdout => new UTF8Encoding(false).GetString(StdoutBytes);
}

/// <summary>
/// Runs the whole application in-process: real argument parsing, real manifest loading, real handlers and real shell
/// emitters, with only the outside world faked (the secret provider, the process launcher and the terminal).
/// </summary>
internal sealed class CliHarness : IDisposable
{
    private const string ManifestTemplate = """
        {
          __DEFAULT__
          "profiles": {
            "dev": {
              "providers": { "kv": { "type": "fake" } },
              "variables": {
                "DB_PASSWORD": { "from": "kv", "ref": "db-password" },
                "API_KEY":     { "from": "kv", "ref": "api-key" },
                "LOG_LEVEL":   { "from": "kv", "ref": "log-level", "required": false }
              }
            },
            "staging": {
              "providers": { "kv": { "type": "fake" } },
              "variables": {
                "DB_PASSWORD": { "from": "kv", "ref": "db-password-staging" }
              }
            }
          }
        }
        """;

    public const string DbSecret = "S3CR3T-db-p@ss";
    public const string ApiSecret = "K3Y-api-token";

    private readonly TempCliDirectory _directory = new();

    public CliHarness()
        : this(Manifest("dev"))
    {
    }

    public CliHarness(string? manifest)
    {
        Provider = new FakeSecretProvider().Returns("db-password", DbSecret).Returns("api-key", ApiSecret);

        if (manifest is not null)
        {
            _directory.Write("envsync.json", manifest);
        }
    }

    /// <summary>The two-profile manifest (dev, staging), with or without a <c>defaultProfile</c>.</summary>
    public static string Manifest(string? defaultProfile) =>
        ManifestTemplate.Replace(
            "__DEFAULT__",
            defaultProfile is null ? string.Empty : $"\"defaultProfile\": \"{defaultProfile}\",",
            StringComparison.Ordinal);

    public FakeSecretProvider Provider { get; }

    public FakeProcessLauncher Launcher { get; } = new();

    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    public bool StdoutIsRedirected { get; set; } = true;

    /// <summary>Replaces stdout with a stream of the test's choosing, for example one that fails like a closed pipe.</summary>
    public Func<Stream>? StandardOutputFactory { get; set; }

    public string Directory => _directory.Path;

    public string Write(string relativePath, string content) => _directory.Write(relativePath, content);

    public Task<CliResult> RunAsync(params string[] args) => RunAsync(CancellationToken.None, args);

    public Task<CliResult> RunWithLauncherAsync(IProcessLauncher launcher, params string[] args) =>
        RunCoreAsync(launcher, args, CancellationToken.None);

    public Task<CliResult> RunAsync(CancellationToken cancellationToken, params string[] args) =>
        RunCoreAsync(Launcher, args, cancellationToken);

    private async Task<CliResult> RunCoreAsync(IProcessLauncher launcher, string[] args, CancellationToken cancellationToken)
    {
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();

        var environment = new CliEnvironment
        {
            StandardOutput = StandardOutputFactory?.Invoke() ?? stdout,
            StandardError = stderr,
            IsOutputRedirected = StdoutIsRedirected,
            GetEnvironmentVariable = name => Environment.GetValueOrDefault(name),
            CurrentDirectory = Directory,
        };

        var services = CompositionRoot.Build(
            environment,
            new CompositionOverrides
            {
                ProviderFactories = [FakeSecretProviderFactory.Returning("fake", Provider)],
                ProcessLauncher = launcher,
            });

        var exitCode = await CliApplication.RunAsync(args, environment, services, cancellationToken);
        return new CliResult(exitCode, stdout.ToArray(), stderr.ToString());
    }

    public void Dispose() => _directory.Dispose();
}

internal sealed class TempCliDirectory : IDisposable
{
    public TempCliDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "envsync-cli-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relativePath, string content)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
