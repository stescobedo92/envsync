using System.Diagnostics;
using System.Net;
using System.Text;
using EnvSync.Application;

namespace EnvSync.Cli.Tests;

/// <summary>
/// The real thing, end to end: the built <c>envsync</c> binary, its real argument parser, the real Vault provider talking HTTP to
/// a fake Vault on loopback, the real process launcher starting a real child, and real pipes for stdout and stderr.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private const string Token = "hvs.e2e-token";
    private const string Password = "e2e p@ss 'quoted' $HOME";

    private static readonly string Envsync = Path.Combine(AppContext.BaseDirectory, "envsync" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
    private static readonly string Child = Path.Combine(AppContext.BaseDirectory, "EnvSync.TestChild" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    private readonly HttpListener _vault = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _address;
    private readonly TempCliDirectory _work = new();
    private readonly Task _serving;

    public EndToEndTests()
    {
        var port = FreePort();
        _address = $"http://127.0.0.1:{port}";
        _vault.Prefixes.Add(_address + "/");
        _vault.Start();
        _serving = Task.Run(ServeAsync);
    }

    [Fact]
    public async Task Run_StartsARealChildWithTheSecretInItsEnvironmentAndPassesItsExitCodeThrough()
    {
        var manifest = WriteManifest();
        var report = Path.Combine(_work.Path, "child-report.txt");

        var result = await RunEnvsyncAsync(
            ["run", "--manifest", manifest, "--profile", "dev", "--", Child, report, "exit=3", "arg with spaces"],
            extraEnvironment: new() { ["PROBE_VARS"] = "DB_PASSWORD" });

        Assert.Equal(3, result.ExitCode);
        var lines = await File.ReadAllLinesAsync(report, TestContext.Current.CancellationToken);
        var line = lines.Single(l => l.StartsWith("ENV:DB_PASSWORD=", StringComparison.Ordinal));
        Assert.Equal(Password, Encoding.UTF8.GetString(Convert.FromBase64String(line["ENV:DB_PASSWORD=".Length..])));
        Assert.Contains(lines, l => l == "ARG:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("arg with spaces")));
        Assert.DoesNotContain(Password, result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenAKeyIsMissing_TheChildIsNeverStartedAndTheExitCodeIs11()
    {
        var manifest = WriteManifest(includeMissing: true);
        var report = Path.Combine(_work.Path, "never.txt");

        var result = await RunEnvsyncAsync(["run", "--manifest", manifest, "--profile", "dev", "--", Child, report, "exit=0"]);

        Assert.Equal(ExitCodes.MissingRequired, result.ExitCode);
        Assert.False(File.Exists(report));
        Assert.Contains("NOT_THERE", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("MISSING", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_AgainstAWrongToken_ExitsWith12()
    {
        var manifest = WriteManifest();

        var result = await RunEnvsyncAsync(["check", "--manifest", manifest, "--profile", "dev"], vaultToken: "hvs.wrong");

        Assert.Equal(ExitCodes.ProviderFailure, result.ExitCode);
        Assert.Contains("AUTH", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_WithoutAnyVaultToken_SaysHowToProvideOne()
    {
        var manifest = WriteManifest();

        var result = await RunEnvsyncAsync(["check", "--manifest", manifest, "--profile", "dev"], vaultToken: null);

        Assert.Equal(ExitCodes.ProviderFailure, result.ExitCode);
        Assert.Contains("VAULT_TOKEN", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_ThroughARealPipe_EmitsAScriptForTheRequestedShell()
    {
        var manifest = WriteManifest();

        var result = await RunEnvsyncAsync(["env", "--manifest", manifest, "--profile", "dev", "--shell", "bash"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("export DB_PASSWORD='e2e p@ss '\\''quoted'\\'' $HOME'\n", result.Stdout);
        Assert.DoesNotContain(Password, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_WhenStdoutIsNotRedirected_ThereIsNoWayToTestATtyHere_SoItStaysUsableInPipes()
    {
        // Our capture redirects stdout, which is exactly the supported use (`envsync env | ...`); this documents it.
        var manifest = WriteManifest();

        var result = await RunEnvsyncAsync(["env", "--manifest", manifest, "--profile", "dev", "--shell", "pwsh"]);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("$env:DB_PASSWORD = ", result.Stdout, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _vault.Stop();
        _vault.Close();
        _serving.Wait(TimeSpan.FromSeconds(5));
        _stop.Dispose();
        _work.Dispose();
    }

    private string WriteManifest(bool includeMissing = false)
    {
        var missing = includeMissing ? ", \"NOT_THERE\": { \"from\": \"vault\", \"ref\": \"app#nope\" }" : string.Empty;
        return _work.Write("envsync.json", $$"""
            {
              "profiles": {
                "dev": {
                  "providers": { "vault": { "type": "hashicorp-vault", "address": "{{_address}}" } },
                  "variables": { "DB_PASSWORD": { "from": "vault", "ref": "app#password" }{{missing}} }
                }
              }
            }
            """);
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _vault.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            Respond(context);
        }
    }

    private static void Respond(HttpListenerContext context)
    {
        const string found = """{"data":{"data":{"password":"__PASSWORD__"},"metadata":{"version":1}}}""";
        var (status, body) = context.Request.Headers["X-Vault-Token"] != Token
            ? (403, """{"errors":["permission denied"]}""")
            : context.Request.Url!.AbsolutePath == "/v1/secret/data/app"
                ? (200, found.Replace("__PASSWORD__", Password, StringComparison.Ordinal))
                : (404, """{"errors":[]}""");

        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunEnvsyncAsync(
        string[] args,
        string? vaultToken = Token,
        Dictionary<string, string>? extraEnvironment = null)
    {
        var startInfo = new ProcessStartInfo(Envsync)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // A hermetic environment: nothing from the developer's machine may leak into the run.
        startInfo.Environment.Remove("VAULT_TOKEN");
        startInfo.Environment.Remove("VAULT_ADDR");
        startInfo.Environment.Remove("VAULT_NAMESPACE");
        startInfo.Environment.Remove("ENVSYNC_PROFILE");
        startInfo.Environment["HOME"] = Path.GetTempPath();
        startInfo.Environment["USERPROFILE"] = Path.GetTempPath();
        if (vaultToken is not null)
        {
            startInfo.Environment["VAULT_TOKEN"] = vaultToken;
        }

        foreach (var (name, value) in extraEnvironment ?? [])
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
