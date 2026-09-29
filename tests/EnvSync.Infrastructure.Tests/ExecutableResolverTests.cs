using EnvSync.Infrastructure.Processes;

namespace EnvSync.Infrastructure.Tests;

public sealed class ExecutableResolverTests
{
    private static readonly string[] WindowsExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    private static string Make(TempDirectory dir, string relativePath, bool executable = true)
    {
        var path = dir.WriteFile(relativePath, "stub");
        if (executable && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static string PathOf(params string[] directories) => string.Join(Path.PathSeparator, directories);

    [Fact]
    public void Resolve_ExplicitPath_ReturnsItWhenTheFileExists()
    {
        using var dir = new TempDirectory();
        var tool = Make(dir, "bin/tool");

        var resolved = new ExecutableResolver(pathVariable: null, extensions: []).Resolve(tool);

        Assert.Equal(tool, resolved);
    }

    [Fact]
    public void Resolve_ExplicitPathThatDoesNotExist_ReturnsNull()
    {
        using var dir = new TempDirectory();

        var resolved = new ExecutableResolver(null, []).Resolve(dir.Combine("bin", "missing"));

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_BareName_IsSearchedInPathOrder()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var wanted = Make(first, "tool");
        Make(second, "tool");

        var resolved = new ExecutableResolver(PathOf(first.Path, second.Path), []).Resolve("tool");

        Assert.Equal(wanted, resolved);
    }

    [Fact]
    public void Resolve_BareName_SkipsEmptyAndMissingPathEntries()
    {
        using var real = new TempDirectory();
        var tool = Make(real, "tool");
        var path = PathOf(string.Empty, Path.Combine(real.Path, "does-not-exist"), real.Path);

        var resolved = new ExecutableResolver(path, []).Resolve("tool");

        Assert.Equal(tool, resolved);
    }

    [Fact]
    public void Resolve_BareNameNotOnPath_ReturnsNull()
    {
        using var dir = new TempDirectory();
        Make(dir, "other");

        Assert.Null(new ExecutableResolver(PathOf(dir.Path), []).Resolve("tool"));
    }

    [Fact]
    public void Resolve_WithNoPathAtAll_OnlyExplicitPathsResolve()
    {
        Assert.Null(new ExecutableResolver(null, []).Resolve("tool"));
        Assert.Null(new ExecutableResolver(string.Empty, []).Resolve("tool"));
    }

    [Fact]
    public void Resolve_BareNameWithPathExt_FindsTheCmdShim()
    {
        using var dir = new TempDirectory();
        var shim = Make(dir, "npm.CMD");

        var resolved = new ExecutableResolver(PathOf(dir.Path), WindowsExtensions).Resolve("npm");

        Assert.Equal(shim, resolved);
    }

    [Fact]
    public void Resolve_PathExtOrderDecidesWhichExtensionWins()
    {
        using var dir = new TempDirectory();
        var exe = Make(dir, "tool.EXE");
        Make(dir, "tool.CMD");

        var resolved = new ExecutableResolver(PathOf(dir.Path), WindowsExtensions).Resolve("tool");

        Assert.Equal(exe, resolved);
    }

    [Fact]
    public void Resolve_NameThatAlreadyHasAKnownExtension_IsTriedAsIs()
    {
        using var dir = new TempDirectory();
        var script = Make(dir, "build.cmd");

        var resolved = new ExecutableResolver(PathOf(dir.Path), WindowsExtensions).Resolve("build.cmd");

        Assert.Equal(script, resolved);
    }

    [Fact]
    public void Resolve_ExtensionlessFileIsNotRunnableWhenPathExtIsInForce()
    {
        using var dir = new TempDirectory();
        Make(dir, "git");

        Assert.Null(new ExecutableResolver(PathOf(dir.Path), WindowsExtensions).Resolve("git"));
    }

    [Fact]
    public void Resolve_ExplicitPathWithoutExtension_GetsPathExtAppended()
    {
        using var dir = new TempDirectory();
        var shim = Make(dir, "bin/tool.CMD");

        var resolved = new ExecutableResolver(null, WindowsExtensions).Resolve(dir.Combine("bin", "tool"));

        Assert.Equal(shim, resolved);
    }

    [Fact]
    public void Resolve_DirectoryWithTheSameName_IsNotAnExecutable()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine("tool"));

        Assert.Null(new ExecutableResolver(PathOf(dir.Path), []).Resolve("tool"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankCommand_ReturnsNull(string command)
    {
        Assert.Null(new ExecutableResolver(null, []).Resolve(command));
    }

    [Fact]
    public void Resolve_OnUnix_AFileWithoutTheExecuteBitIsSkipped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permission bits do not exist on Windows.");
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        Make(first, "tool", executable: false);
        var runnable = Make(second, "tool");

        var resolved = new ExecutableResolver(PathOf(first.Path, second.Path), []).Resolve("tool");

        Assert.Equal(runnable, resolved);
    }

    [Fact]
    public void ForCurrentProcess_ResolvesTheDotnetHostThatIsRunningTheTests()
    {
        var resolved = ExecutableResolver.ForCurrentProcess().Resolve("dotnet");

        Assert.NotNull(resolved);
    }
}
