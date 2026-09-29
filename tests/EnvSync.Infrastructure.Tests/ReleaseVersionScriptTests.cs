using System.Diagnostics;

namespace EnvSync.Infrastructure.Tests;

/// <summary>
/// The release workflow publishes whatever <c>scripts/release/verify-version.sh</c> lets through, so the script must refuse anything
/// that is not a strict SemVer 2.0.0 tag, or that does not match the version compiled into the tool. It is run in a real bash
/// (skipped where there is none), because the point is the behavior of the script the workflow will actually execute.
/// </summary>
public sealed class ReleaseVersionScriptTests
{
    private const int Refused = 1;
    private const int UsageError = 64;

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "envsync.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("envsync.slnx was not found above the test binaries.");
    }

    private static string ProjectXml(string? version) =>
        version is null
            ? "<Project><PropertyGroup><PackAsTool>true</PackAsTool></PropertyGroup></Project>"
            : $"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>";

    /// <summary>Git Bash wants forward slashes, and every other bash accepts them.</summary>
    private static string ForBash(string path) => path.Replace('\\', '/');

    private static async Task<ScriptResult> RunScriptAsync(string workingDirectory, params string[] arguments)
    {
        var bash = ShellHarness.FindBash();
        Assert.SkipUnless(bash is not null, "bash is not installed on this machine.");

        var startInfo = new ProcessStartInfo(bash!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        startInfo.ArgumentList.Add("--noprofile");
        startInfo.ArgumentList.Add("--norc");
        startInfo.ArgumentList.Add(ForBash(Path.Combine(RepositoryRoot(), "scripts", "release", "verify-version.sh")));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start bash.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(60));

        var stdout = process.StandardOutput.ReadToEndAsync(budget.Token);
        var stderr = process.StandardError.ReadToEndAsync(budget.Token);
        await process.WaitForExitAsync(budget.Token);

        return new ScriptResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>Runs the script the way the workflow does: a tag (empty for a dry run) and the project file that carries the version.</summary>
    private static async Task<ScriptResult> VerifyAsync(string tag, string? projectVersion)
    {
        using var directory = new TempDirectory();
        var project = directory.WriteFile("EnvSync.Cli.csproj", ProjectXml(projectVersion));

        return await RunScriptAsync(directory.Path, tag, ForBash(project));
    }

    [Theory]
    [InlineData("v0.1.0", "0.1.0", "false")]
    [InlineData("v1.2.3", "1.2.3", "false")]
    [InlineData("v10.20.30", "10.20.30", "false")]
    [InlineData("v1.0.0-rc.1", "1.0.0-rc.1", "true")]
    [InlineData("v1.0.0-alpha", "1.0.0-alpha", "true")]
    [InlineData("v1.0.0-0.3.7", "1.0.0-0.3.7", "true")]
    [InlineData("v1.0.0-x.7.z.92", "1.0.0-x.7.z.92", "true")]
    [InlineData("v1.0.0-alpha-a.b-c", "1.0.0-alpha-a.b-c", "true")]
    public async Task Verify_TagThatMatchesTheProjectVersion_PrintsTheVersionAndWhetherItIsAPrerelease(string tag, string version, string prerelease)
    {
        var result = await VerifyAsync(tag, version);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"version={version}\nprerelease={prerelease}\n", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("V0.1.0")]
    [InlineData("vv1.2.3")]
    [InlineData("v1")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("v01.2.3")]
    [InlineData("v1.02.3")]
    [InlineData("v1.2.03")]
    [InlineData("v1.2.3-")]
    [InlineData("v1.2.3-01")]
    [InlineData("v1.2.3-rc.01")]
    [InlineData("v1.2.3-beta..1")]
    [InlineData("v1.2.3-rc_1")]
    [InlineData("v1.2.3 ")]
    [InlineData(" v1.2.3")]
    [InlineData("refs/tags/v1.2.3")]
    [InlineData("latest")]
    public async Task Verify_TagThatIsNotStrictSemVer_IsRefusedWithoutPrintingAnything(string tag)
    {
        var result = await VerifyAsync(tag, "1.2.3");

        Assert.Equal(Refused, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("SemVer", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v1.2.3+build5")]
    [InlineData("v1.0.0-rc.1+build5")]
    public async Task Verify_TagWithBuildMetadata_IsRefusedBecauseTwoTagsWouldBeTheSameVersion(string tag)
    {
        var result = await VerifyAsync(tag, "1.2.3");

        Assert.Equal(Refused, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("build metadata", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v1.2.3\nprerelease=false")]
    [InlineData("v1.2.3\n")]
    [InlineData("v9.9.9\nversion=1.2.3")]
    public async Task Verify_TagWithALineBreak_CannotSmuggleAnExtraOutputLine(string tag)
    {
        var result = await VerifyAsync(tag, "1.2.3");

        Assert.Equal(Refused, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Theory]
    [InlineData("v1.2.3;touch pwned")]
    [InlineData("v1.2.3$(touch pwned)")]
    [InlineData("v1.2.3`touch pwned`")]
    [InlineData("v1.2.3-rc.1&&touch pwned")]
    public async Task Verify_TagThatLooksLikeACommand_IsRefusedAndNeverExecuted(string tag)
    {
        using var directory = new TempDirectory();
        var project = directory.WriteFile("EnvSync.Cli.csproj", ProjectXml("1.2.3"));

        var result = await RunScriptAsync(directory.Path, tag, ForBash(project));

        Assert.Equal(Refused, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.False(File.Exists(directory.Combine("pwned")), "the tag was executed as a command");
    }

    [Theory]
    [InlineData("v1.2.4", "1.2.3")]
    [InlineData("v2.0.0", "1.9.9")]
    [InlineData("v1.0.0", "1.0.0-rc.1")]
    [InlineData("v1.0.0-rc.2", "1.0.0-rc.1")]
    public async Task Verify_TagThatDiffersFromTheProjectVersion_IsRefusedAndNamesBoth(string tag, string projectVersion)
    {
        var result = await VerifyAsync(tag, projectVersion);

        Assert.Equal(Refused, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains(tag, result.Stderr, StringComparison.Ordinal);
        Assert.Contains(projectVersion, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_WhitespaceAroundTheProjectVersion_IsIgnoredJustAsMsBuildDoes()
    {
        var result = await VerifyAsync("v1.2.3", "  1.2.3  ");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("version=1.2.3\nprerelease=false\n", result.Stdout);
    }

    [Theory]
    [InlineData("0.1.0", "false")]
    [InlineData("2.0.0-beta.2", "true")]
    public async Task Verify_EmptyTag_IsADryRunThatReadsTheVersionFromTheProject(string projectVersion, string prerelease)
    {
        var result = await VerifyAsync(string.Empty, projectVersion);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"version={projectVersion}\nprerelease={prerelease}\n", result.Stdout);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0+meta")]
    [InlineData("$(BaseVersion)")]
    public async Task Verify_ProjectVersionThatIsMissingOrNotSemVer_IsRefusedEvenOnADryRun(string? projectVersion)
    {
        var result = await VerifyAsync(string.Empty, projectVersion);

        Assert.Equal(Refused, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("<Version>", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_MissingProjectFile_IsRefused()
    {
        using var directory = new TempDirectory();

        var result = await RunScriptAsync(directory.Path, "v1.2.3", ForBash(directory.Combine("nope.csproj")));

        Assert.Equal(Refused, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("nope.csproj", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("v1.2.3")]
    [InlineData("v1.2.3", "a.csproj", "extra")]
    public async Task Verify_WrongNumberOfArguments_IsAUsageError(params string[] arguments)
    {
        using var directory = new TempDirectory();

        var result = await RunScriptAsync(directory.Path, arguments);

        Assert.Equal(UsageError, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("usage", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }
}
