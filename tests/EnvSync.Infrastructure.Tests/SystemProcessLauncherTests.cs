using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;
using EnvSync.Infrastructure.Processes;

namespace EnvSync.Infrastructure.Tests;

public sealed class SystemProcessLauncherTests
{
    private static readonly string Child = Path.Combine(
        AppContext.BaseDirectory,
        "EnvSync.TestChild" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    private sealed record ChildReport(int Pid, Dictionary<string, string?> Environment, List<string> Arguments);

    private static SystemProcessLauncher LauncherWith(TimeSpan? grace = null) =>
        new(ExecutableResolver.ForCurrentProcess(), grace ?? TimeSpan.FromSeconds(10));

    private static ProcessLaunchRequest Request(
        string executable,
        ImmutableArray<string> arguments,
        params (string Name, string Value)[] environment) =>
        new(
            executable,
            arguments,
            [.. environment.Select(e => new EnvironmentAssignment(EmitterAssert.Name(e.Name), new SecretValue(e.Value)))]);

    private static ImmutableArray<string> ChildArgs(string reportFile, string mode, params string[] extra) =>
        [reportFile, mode, .. extra];

    private static async Task<ChildReport> ReadReportAsync(string file, CancellationToken cancellationToken)
    {
        // The child may still be flushing when we look: retry until its PID line is there.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(file, cancellationToken);
                if (lines.Any(l => l.StartsWith("PID:", StringComparison.Ordinal)))
                {
                    return Parse(lines);
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException($"The child never produced {file}.");
    }

    private static ChildReport Parse(string[] lines)
    {
        var pid = 0;
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var arguments = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("PID:", StringComparison.Ordinal))
            {
                pid = int.Parse(line.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("ENV:", StringComparison.Ordinal))
            {
                var separator = line.IndexOf('=', StringComparison.Ordinal);
                var value = line[(separator + 1)..];
                environment[line[4..separator]] = value == "<unset>" ? null : Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            else if (line.StartsWith("ARG:", StringComparison.Ordinal))
            {
                arguments.Add(Encoding.UTF8.GetString(Convert.FromBase64String(line[4..])));
            }
        }

        return new ChildReport(pid, environment, arguments);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Run_PutsTheSecretsInTheChildEnvironmentAndNowhereElse()
    {
        using var dir = new TempDirectory();
        var report = dir.Combine("report.txt");
        var request = Request(
            Child,
            ChildArgs(report, "exit=0"),
            ("PROBE_VARS", "ENVSYNC_PROBE_A;ENVSYNC_PROBE_B"),
            ("ENVSYNC_PROBE_A", "s3cr3t"),
            ("ENVSYNC_PROBE_B", "multi\nline é 日本語"));

        var result = await LauncherWith().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var seen = await ReadReportAsync(report, TestContext.Current.CancellationToken);
        Assert.Equal("s3cr3t", seen.Environment["ENVSYNC_PROBE_A"]);
        Assert.Equal("multi\nline é 日本語", seen.Environment["ENVSYNC_PROBE_B"]);
        Assert.Null(Environment.GetEnvironmentVariable("ENVSYNC_PROBE_A"));
        Assert.Null(Environment.GetEnvironmentVariable("ENVSYNC_PROBE_B"));
    }

    [Fact]
    public async Task Run_PassesArgumentsVerbatim()
    {
        using var dir = new TempDirectory();
        var report = dir.Combine("report.txt");
        string[] extra = ["a b", "it's", "\"quoted\"", "--flag=1", "üñí", "*", "$HOME", "%PATH%", "trailing\\"];

        var result = await LauncherWith().RunAsync(
            Request(Child, ChildArgs(report, "exit=0", extra)),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var seen = await ReadReportAsync(report, TestContext.Current.CancellationToken);
        Assert.Equal(extra, seen.Arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(11)]
    public async Task Run_ReturnsTheChildExitCode(int exitCode)
    {
        using var dir = new TempDirectory();

        var result = await LauncherWith().RunAsync(
            Request(Child, ChildArgs(dir.Combine("r.txt"), $"exit={exitCode}")),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(exitCode, result.Value);
    }

    [Fact]
    public async Task Run_TheChildInheritsTheRestOfTheEnvironment()
    {
        using var dir = new TempDirectory();
        var report = dir.Combine("report.txt");

        await LauncherWith().RunAsync(
            Request(Child, ChildArgs(report, "exit=0"), ("PROBE_VARS", "PATH")),
            TestContext.Current.CancellationToken);

        var seen = await ReadReportAsync(report, TestContext.Current.CancellationToken);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), seen.Environment["PATH"]);
    }

    [Fact]
    public async Task Run_ASecretOverridesAnInheritedVariable()
    {
        using var dir = new TempDirectory();
        var report = dir.Combine("report.txt");
        var original = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var overridden = original + Path.PathSeparator + "envsync-extra";

        await LauncherWith().RunAsync(
            Request(Child, ChildArgs(report, "exit=0"), ("PROBE_VARS", "PATH"), ("PATH", overridden)),
            TestContext.Current.CancellationToken);

        var seen = await ReadReportAsync(report, TestContext.Current.CancellationToken);
        Assert.Equal(overridden, seen.Environment["PATH"]);
        Assert.Equal(original, Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
    }

    [Fact]
    public async Task Run_NamesToUnset_AreRemovedEvenWhenTheParentEnvironmentHasThem()
    {
        var stale = "ENVSYNC_STALE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(stale, "stale-value-from-an-earlier-session");
        try
        {
            using var dir = new TempDirectory();
            var report = dir.Combine("report.txt");
            var request = new ProcessLaunchRequest(
                Child,
                ChildArgs(report, "exit=0"),
                [new EnvironmentAssignment(EmitterAssert.Name("PROBE_VARS"), new SecretValue(stale))],
                [EmitterAssert.Name(stale)]);

            var result = await LauncherWith().RunAsync(request, TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            var seen = await ReadReportAsync(report, TestContext.Current.CancellationToken);
            Assert.Null(seen.Environment[stale]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(stale, null);
        }
    }

    [Fact]
    public async Task Run_ExecutableThatDoesNotExist_ReportsExecutableNotFound()
    {
        var result = await LauncherWith().RunAsync(
            Request("definitely-not-installed-envsync-xyz", []),
            TestContext.Current.CancellationToken);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ExecutableNotFound, error.Kind);
        Assert.Equal("definitely-not-installed-envsync-xyz", error.Subject);
    }

    [Fact]
    public async Task Run_ValueThatCannotLiveInAnEnvironmentBlock_IsRefusedWithoutStartingTheChild()
    {
        using var dir = new TempDirectory();
        var report = dir.Combine("report.txt");

        var result = await LauncherWith().RunAsync(
            Request(Child, ChildArgs(report, "exit=0"), ("ENVSYNC_PROBE_A", "bad\0value")),
            TestContext.Current.CancellationToken);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProcessLaunchFailed, error.Kind);
        Assert.Equal("ENVSYNC_PROBE_A", error.Subject);
        Assert.DoesNotContain("bad", error.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(report));
    }

    [Fact]
    public async Task Run_CancelledWhileTheChildRuns_StopsItOnceTheGracePeriodEnds()
    {
        using var dir = new TempDirectory();
        var report = dir.Combine("report.txt");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = LauncherWith(TimeSpan.FromMilliseconds(300))
            .RunAsync(Request(Child, ChildArgs(report, "hang")), cts.Token)
            .AsTask();

        var seen = await ReadReportAsync(report, TestContext.Current.CancellationToken);
        Assert.True(IsRunning(seen.Pid));
        await cts.CancelAsync();

        var result = await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(0, result.Value);
        Assert.False(IsRunning(seen.Pid));
    }

    [Fact]
    public async Task Run_OnWindows_ARunsACmdShimFoundThroughPathExt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Batch shims such as npm.cmd only exist on Windows.");
        using var dir = new TempDirectory();
        dir.WriteFile("fake-npm.cmd", "@echo off\r\nexit /b 5\r\n");
        var launcher = new SystemProcessLauncher(new ExecutableResolver(dir.Path, [".COM", ".EXE", ".BAT", ".CMD"]), TimeSpan.FromSeconds(10));

        var result = await launcher.RunAsync(Request("fake-npm", []), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value);
    }

    private static SystemProcessLauncher LauncherOverShimsIn(TempDirectory dir) =>
        new(new ExecutableResolver(dir.Path, [".COM", ".EXE", ".BAT", ".CMD"]), TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Run_OnWindows_ACmdShimReceivesASafeArgumentVerbatim()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Batch files only exist on Windows.");
        using var dir = new TempDirectory();
        dir.WriteFile("echo-arg.cmd", "@echo off\r\n(echo %~1)>\"%~dp0arg.txt\"\r\n");

        var result = await LauncherOverShimsIn(dir).RunAsync(Request("echo-arg", ["hello world"]), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello world", (await File.ReadAllTextAsync(dir.Combine("arg.txt"), TestContext.Current.CancellationToken)).Trim());
    }

    // cmd.exe re-parses the command line of a batch file with its own rules, not the ones .NET quotes for: `&` starts a new
    // command, `%VAR%` expands (even inside quotes, and the environment holds the secrets envsync just injected), and a quote can
    // close the quoting. Such arguments are refused rather than escaped, because no escaping is correct for all of them.
    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("a>b")]
    [InlineData("a<b")]
    [InlineData("a^b")]
    [InlineData("a!b")]
    [InlineData("(a)")]
    [InlineData("a\"b")]
    [InlineData("%PATH%")]
    [InlineData("100%")]
    [InlineData("x\" & echo INJECTED > injected.txt & \"")]
    public async Task Run_OnWindows_ACmdShimWithAnArgumentCmdWouldInterpret_IsRefusedBeforeAnythingRuns(string argument)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Batch files only exist on Windows.");
        using var dir = new TempDirectory();
        dir.WriteFile("echo-arg.cmd", "@echo off\r\n(echo %~1)>\"%~dp0arg.txt\"\r\n");
        dir.WriteFile("tool.bat", "@echo off\r\nexit /b 0\r\n");

        var viaCmd = await LauncherOverShimsIn(dir).RunAsync(Request("echo-arg", [argument]), TestContext.Current.CancellationToken);
        var viaBat = await LauncherOverShimsIn(dir).RunAsync(Request("tool.bat", [argument]), TestContext.Current.CancellationToken);

        foreach (var result in new[] { viaCmd, viaBat })
        {
            var error = Assert.Single(result.Errors);
            Assert.Equal(ErrorKind.ProcessLaunchFailed, error.Kind);
            Assert.Contains("cmd.exe", error.Detail, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(dir.Combine("arg.txt")));
        Assert.False(File.Exists(dir.Combine("injected.txt")));
    }

    [Fact]
    public async Task Run_AnExecutableIsNeverSubjectToTheBatchFileRule()
    {
        using var dir = new TempDirectory();

        var result = await LauncherWith().RunAsync(
            Request(Child, ChildArgs(dir.Combine("r.txt"), "exit=0", "a&b", "%PATH%", "x\" & y")),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Run_AlreadyCancelled_ThrowsBeforeTheChildIsStarted()
    {
        using var dir = new TempDirectory();
        var report = dir.Combine("report.txt");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await LauncherWith().RunAsync(Request(Child, ChildArgs(report, "exit=0")), cts.Token));

        Assert.False(File.Exists(report));
    }

    [Fact]
    public async Task Run_OnWindows_AFileThatIsNotAnExecutable_ReportsALaunchFailure()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows resolves and then rejects a non-PE .exe.");
        using var dir = new TempDirectory();
        var broken = dir.WriteFile("broken.exe", "this is not a portable executable");

        var result = await LauncherWith().RunAsync(Request(broken, []), TestContext.Current.CancellationToken);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProcessLaunchFailed, error.Kind);
    }
}
