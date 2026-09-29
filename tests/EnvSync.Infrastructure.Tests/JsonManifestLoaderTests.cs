using EnvSync.Application.Providers;
using EnvSync.Domain;
using EnvSync.Infrastructure.Manifests;

namespace EnvSync.Infrastructure.Tests;

public sealed class JsonManifestLoaderTests
{
    private const string Minimal = """{"profiles": {"dev": {"providers": {}, "variables": {}}}}""";

    private static JsonManifestLoader Loader() => new(new ProviderRegistry([]));

    private static ValueTask<Result<Manifest>> Load(JsonManifestLoader loader, string? explicitPath, string startDirectory) =>
        loader.LoadAsync(explicitPath, startDirectory, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Load_ExplicitPath_IsResolvedAgainstTheStartDirectory()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("config/custom.json", Minimal);

        var result = await Load(Loader(), Path.Combine("config", "custom.json"), dir.Path);

        Assert.True(result.IsSuccess);
        Assert.Equal("dev", Assert.Single(result.Value.Profiles).Name);
    }

    [Fact]
    public async Task Load_AbsoluteExplicitPath_IgnoresTheStartDirectory()
    {
        using var manifestDir = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var path = manifestDir.WriteFile("m.json", Minimal);

        var result = await Load(Loader(), path, elsewhere.Path);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Load_WithoutAPath_FindsTheManifestInAParentDirectory()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("envsync.json", Minimal);
        var deep = Directory.CreateDirectory(dir.Combine("src", "app", "nested")).FullName;

        var result = await Load(Loader(), null, deep);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Load_TheNearestManifestWins()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("envsync.json", """{"profiles": {"outer": {"providers": {}, "variables": {}}}}""");
        dir.WriteFile("app/envsync.json", """{"profiles": {"inner": {"providers": {}, "variables": {}}}}""");

        var result = await Load(Loader(), null, dir.Combine("app"));

        Assert.Equal("inner", Assert.Single(result.Value.Profiles).Name);
    }

    [Fact]
    public async Task Load_NoManifestAnywhere_ExplainsHowToProvideOne()
    {
        using var dir = new TempDirectory();
        var start = Directory.CreateDirectory(dir.Combine("a", "b")).FullName;

        // The walk reaches the filesystem root, so this only holds when no envsync.json sits above the temp dir.
        var result = await Load(Loader(), null, start);

        if (result.IsSuccess)
        {
            Assert.Skip("An envsync.json exists above the temp directory on this machine.");
        }

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Contains("--manifest", error.Detail, StringComparison.Ordinal);
        Assert.Contains("envsync.json", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_ExplicitPathThatDoesNotExist_NamesTheFile()
    {
        using var dir = new TempDirectory();

        var result = await Load(Loader(), "missing.json", dir.Path);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Contains("missing.json", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_ExplicitPathIsADirectory_IsAnError()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine("a-directory"));

        var result = await Load(Loader(), "a-directory", dir.Path);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Load_ParseErrorsCarryTheFullPathOfTheFile()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteFile("envsync.json", "{ nope");

        var result = await Load(Loader(), null, dir.Path);

        var error = Assert.Single(result.Errors);
        Assert.Equal(path, error.Subject);
    }

    [Fact]
    public async Task Load_ReadsAFileWithAByteOrderMark()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("envsync.json");
        await File.WriteAllTextAsync(path, Minimal, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), TestContext.Current.CancellationToken);

        var result = await Load(Loader(), null, dir.Path);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Load_ValidatesProviderTypesAgainstTheRegistry()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("envsync.json", """{"profiles": {"dev": {"providers": {"x": {"type": "not-registered"}}, "variables": {}}}}""");

        var result = await Load(Loader(), null, dir.Path);

        Assert.Equal(ErrorKind.ProviderUnknown, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task Load_AnAbsurdlyLargeFile_IsRefusedInsteadOfBeingReadIntoMemory()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteFile("envsync.json", string.Empty);
        await using (var stream = File.Create(path))
        {
            stream.SetLength(3 * 1024 * 1024);
        }

        var result = await Load(Loader(), null, dir.Path);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Contains("1 MiB", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_CancelledBeforeReading_Throws()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("envsync.json", Minimal);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Loader().LoadAsync(null, dir.Path, cts.Token));
    }
}
