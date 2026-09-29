using EnvSync.Application.Providers;
using EnvSync.Domain;
using EnvSync.Infrastructure.Manifests;
using EnvSync.TestSupport;

namespace EnvSync.Infrastructure.Tests;

public sealed class ManifestParserTests
{
    private const string Source = "envsync.json";

    private const string Valid = """
        {
          "defaultProfile": "dev",
          "profiles": {
            "dev": {
              "providers": {
                "kv":    { "type": "azure-keyvault", "uri": "https://my-kv.vault.azure.net" },
                "vault": { "type": "hashicorp-vault", "address": "https://vault:8200", "kv": 2 }
              },
              "variables": {
                "DB_PASSWORD": { "from": "kv",    "ref": "db-password" },
                "JWT_SECRET":  { "from": "vault", "ref": "app#jwt" },
                "LOG_LEVEL":   { "from": "kv",    "ref": "log-level", "required": false }
              }
            }
          }
        }
        """;

    private static ProviderRegistry RegistryOf(params string[] types) =>
        new(types.Select(t => FakeSecretProviderFactory.Failing(t, ErrorKind.ProviderMisconfigured)));

    private static ImmutableArrayOfErrors Errors(Result<Manifest> result) => new(result.Errors);

    private readonly record struct ImmutableArrayOfErrors(System.Collections.Immutable.ImmutableArray<Error> Items)
    {
        public bool Has(ErrorKind kind, string subject) => Items.Any(e => e.Kind == kind && e.Subject == subject);
    }

    [Fact]
    public void Parse_ValidManifest_BuildsTheDomainModel()
    {
        var result = ManifestParser.Parse(Valid, Source);

        Assert.True(result.IsSuccess);
        var manifest = result.Value;
        Assert.Equal("dev", manifest.DefaultProfile);
        var profile = Assert.Single(manifest.Profiles);
        Assert.Equal("dev", profile.Name);

        Assert.Equal(2, profile.Providers.Length);
        Assert.Equal("kv", profile.Providers[0].Alias);
        Assert.Equal("azure-keyvault", profile.Providers[0].Type);
        Assert.Equal("https://my-kv.vault.azure.net", profile.Providers[0].Settings["uri"]);

        Assert.Equal(3, profile.Variables.Length);
        Assert.Equal("DB_PASSWORD", profile.Variables[0].Name.Value);
        Assert.Equal("kv", profile.Variables[0].From);
        Assert.Equal("db-password", profile.Variables[0].Reference.Path);
        Assert.Null(profile.Variables[0].Reference.Field);
        Assert.True(profile.Variables[0].Required);
        Assert.Equal("jwt", profile.Variables[1].Reference.Field);
        Assert.False(profile.Variables[2].Required);
    }

    [Fact]
    public void Parse_ScalarSettingsOfAnyJsonTypeBecomeText()
    {
        var result = ManifestParser.Parse(Valid, Source);

        var vault = result.Value.Profiles[0].Providers[1];
        Assert.Equal("2", vault.Settings["kv"]);
    }

    [Fact]
    public void Parse_AllowsCommentsAndTrailingCommas()
    {
        const string json = """
            {
              // which profile to use when none is given
              "profiles": {
                "dev": {
                  "providers": { "kv": { "type": "azure-keyvault", }, },
                  "variables": { "A": { "from": "kv", "ref": "a", }, }, /* trailing */
                },
              },
            }
            """;

        Assert.True(ManifestParser.Parse(json, Source).IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("""{"profiles": {""")]
    public void Parse_MalformedJson_IsAManifestErrorNamingTheFile(string json)
    {
        var result = ManifestParser.Parse(json, Source);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Equal(Source, error.Subject);
    }

    [Fact]
    public void Parse_MalformedJson_ReportsTheLine()
    {
        var result = ManifestParser.Parse("{\n  \"profiles\": {\n    \"dev\": oops\n  }\n}", Source);

        Assert.Contains("line 3", Assert.Single(result.Errors).Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void Parse_RootThatIsNotAnObject_IsRejected(string json)
    {
        var error = Assert.Single(ManifestParser.Parse(json, Source).Errors);

        Assert.Equal(ErrorKind.ManifestInvalid, error.Kind);
        Assert.Contains("object", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"profiles": {}}""")]
    [InlineData("""{"profiles": []}""")]
    public void Parse_WithoutAnyProfile_IsRejected(string json)
    {
        var result = ManifestParser.Parse(json, Source);

        Assert.True(Errors(result).Has(ErrorKind.ManifestInvalid, "profiles"));
    }

    [Fact]
    public void Parse_ProfileThatIsNotAnObject_IsRejected()
    {
        var result = ManifestParser.Parse("""{"profiles": {"dev": 5}}""", Source);

        Assert.True(Errors(result).Has(ErrorKind.ManifestInvalid, "profiles.dev"));
    }

    [Fact]
    public void Parse_UnknownProperties_AreRejectedWithTheirPath()
    {
        const string json = """
            {
              "profile": {},
              "profiles": {
                "dev": {
                  "provider": {},
                  "providers": { "kv": { "type": "azure-keyvault" } },
                  "variables": { "A": { "from": "kv", "ref": "a", "require": false } }
                }
              }
            }
            """;

        var errors = Errors(ManifestParser.Parse(json, Source));

        Assert.True(errors.Has(ErrorKind.ManifestInvalid, "profile"));
        Assert.True(errors.Has(ErrorKind.ManifestInvalid, "profiles.dev.provider"));
        Assert.True(errors.Has(ErrorKind.ManifestInvalid, "profiles.dev.variables.A.require"));
    }

    [Theory]
    [InlineData("""{ "uri": "x" }""", "profiles.dev.providers.kv.type")]
    [InlineData("""{ "type": 5 }""", "profiles.dev.providers.kv.type")]
    [InlineData("""{ "type": "" }""", "profiles.dev.providers.kv.type")]
    [InlineData("""{ "type": "azure-keyvault", "uri": {"a":1} }""", "profiles.dev.providers.kv.uri")]
    [InlineData("""{ "type": "azure-keyvault", "uri": null }""", "profiles.dev.providers.kv.uri")]
    [InlineData("7", "profiles.dev.providers.kv")]
    public void Parse_BadProviders_AreReportedAtTheirPath(string provider, string subject)
    {
        const string template = """{"profiles": {"dev": {"providers": {"kv": __PROVIDER__ }, "variables": {} }}}""";
        var json = template.Replace("__PROVIDER__", provider, StringComparison.Ordinal);

        var result = ManifestParser.Parse(json, Source);

        Assert.True(Errors(result).Has(ErrorKind.ManifestInvalid, subject));
    }

    [Theory]
    [InlineData("""{ "ref": "a" }""", "profiles.dev.variables.A.from")]
    [InlineData("""{ "from": "kv" }""", "profiles.dev.variables.A.ref")]
    [InlineData("""{ "from": 3, "ref": "a" }""", "profiles.dev.variables.A.from")]
    [InlineData("""{ "from": "kv", "ref": "#field" }""", "profiles.dev.variables.A.ref")]
    [InlineData("""{ "from": "kv", "ref": "a", "required": "yes" }""", "profiles.dev.variables.A.required")]
    [InlineData("true", "profiles.dev.variables.A")]
    public void Parse_BadVariables_AreReportedAtTheirPath(string variable, string subject)
    {
        const string template = """{"profiles": {"dev": {"providers": {"kv": {"type": "azure-keyvault"}}, "variables": {"A": __VARIABLE__ }}}}""";
        var json = template.Replace("__VARIABLE__", variable, StringComparison.Ordinal);

        var result = ManifestParser.Parse(json, Source);

        Assert.True(Errors(result).Has(ErrorKind.ManifestInvalid, subject));
    }

    [Theory]
    [InlineData("1BAD")]
    [InlineData("HAS-DASH")]
    [InlineData("HAS SPACE")]
    [InlineData("")]
    public void Parse_InvalidVariableName_IsRejected(string name)
    {
        const string template = """{"profiles": {"dev": {"providers": {"kv": {"type": "azure-keyvault"}}, "variables": {"__NAME__": {"from": "kv", "ref": "a"}}}}}""";
        var json = template.Replace("__NAME__", name, StringComparison.Ordinal);

        var result = ManifestParser.Parse(json, Source);

        Assert.True(Errors(result).Has(ErrorKind.ManifestInvalid, $"profiles.dev.variables.{name}"));
    }

    [Fact]
    public void Parse_VariableUsingAnUndeclaredProvider_IsReported()
    {
        const string json = """{"profiles": {"dev": {"providers": {}, "variables": {"A": {"from": "ghost", "ref": "a"}}}}}""";

        var result = ManifestParser.Parse(json, Source);

        Assert.True(Errors(result).Has(ErrorKind.ProviderUnknown, "A"));
    }

    [Fact]
    public void Parse_DuplicateKeys_AreReported()
    {
        const string json = """
            {
              "profiles": {
                "dev": {
                  "providers": { "kv": { "type": "azure-keyvault" }, "kv": { "type": "aws-secrets" } },
                  "variables": { "A": { "from": "kv", "ref": "a" }, "A": { "from": "kv", "ref": "b" } }
                },
                "dev": { "providers": {}, "variables": {} }
              }
            }
            """;

        var errors = Errors(ManifestParser.Parse(json, Source));

        Assert.True(errors.Has(ErrorKind.ManifestInvalid, "profiles.dev.providers.kv"));
        Assert.True(errors.Has(ErrorKind.ManifestInvalid, "profiles.dev.variables.A"));
        Assert.True(errors.Has(ErrorKind.ManifestInvalid, "profiles.dev"));
    }

    [Fact]
    public void Parse_UnregisteredProviderType_IsReportedWithTheRegisteredOnes()
    {
        const string json = """{"profiles": {"dev": {"providers": {"db": {"type": "oracle-wallet"}}, "variables": {}}}}""";

        var result = ManifestParser.Parse(json, Source, RegistryOf("azure-keyvault", "aws-secrets"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKind.ProviderUnknown, error.Kind);
        Assert.Equal("profiles.dev.providers.db.type", error.Subject);
        Assert.Contains("oracle-wallet", error.Detail, StringComparison.Ordinal);
        Assert.Contains("aws-secrets", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ProviderTypesAreMatchedIgnoringCase()
    {
        const string json = """{"profiles": {"dev": {"providers": {"kv": {"type": "Azure-KeyVault"}}, "variables": {}}}}""";

        Assert.True(ManifestParser.Parse(json, Source, RegistryOf("azure-keyvault")).IsSuccess);
    }

    [Fact]
    public void Parse_WithoutARegistry_DoesNotCheckProviderTypes()
    {
        const string json = """{"profiles": {"dev": {"providers": {"db": {"type": "anything-goes"}}, "variables": {}}}}""";

        Assert.True(ManifestParser.Parse(json, Source).IsSuccess);
    }

    [Fact]
    public void Parse_SeveralProblems_AreAllReportedInOnePass()
    {
        const string json = """
            {
              "profiles": {
                "dev": {
                  "providers": { "kv": { "type": "azure-keyvault" } },
                  "variables": {
                    "1BAD":  { "from": "kv", "ref": "a" },
                    "NOREF": { "from": "kv" },
                    "GHOST": { "from": "nobody", "ref": "g" }
                  }
                }
              }
            }
            """;

        var errors = Errors(ManifestParser.Parse(json, Source));

        Assert.True(errors.Has(ErrorKind.ManifestInvalid, "profiles.dev.variables.1BAD"));
        Assert.True(errors.Has(ErrorKind.ManifestInvalid, "profiles.dev.variables.NOREF.ref"));
        Assert.True(errors.Has(ErrorKind.ProviderUnknown, "GHOST"));
        Assert.Equal(3, errors.Items.Length);
    }

    [Fact]
    public void Parse_KeepsSeveralProfilesInDeclarationOrder()
    {
        const string json = """
            {
              "profiles": {
                "zeta":  { "providers": {}, "variables": {} },
                "alpha": { "providers": {}, "variables": {} }
              }
            }
            """;

        var manifest = ManifestParser.Parse(json, Source).Value;

        Assert.Equal(["zeta", "alpha"], manifest.Profiles.Select(p => p.Name));
    }

    [Fact]
    public void Parse_ErrorDetailsNeverEchoTheRestOfTheFile()
    {
        var result = ManifestParser.Parse("""{"profiles": {"dev": {"providers": {"kv": {"type": "azure-keyvault", "token": {"a": "hunter2-do-not-leak"}}}, "variables": {}}}}""", Source);

        Assert.All(result.Errors, error => Assert.DoesNotContain("hunter2-do-not-leak", error.Detail, StringComparison.Ordinal));
    }
}
