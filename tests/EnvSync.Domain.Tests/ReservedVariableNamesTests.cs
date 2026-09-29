using EnvSync.Domain;

namespace EnvSync.Domain.Tests;

public sealed class ReservedVariableNamesTests
{
    [Theory]
    [InlineData("PATH")]
    [InlineData("path")]
    [InlineData("Path")]
    [InlineData("PATHEXT")]
    [InlineData("COMSPEC")]
    [InlineData("PSModulePath")]
    [InlineData("IFS")]
    [InlineData("ENV")]
    [InlineData("BASH_ENV")]
    [InlineData("PROMPT_COMMAND")]
    [InlineData("PS1")]
    [InlineData("PS4")]
    [InlineData("SHELLOPTS")]
    [InlineData("LD_PRELOAD")]
    [InlineData("ld_library_path")]
    [InlineData("DYLD_INSERT_LIBRARIES")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("PYTHONSTARTUP")]
    [InlineData("JAVA_TOOL_OPTIONS")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("GIT_SSH_COMMAND")]
    public void IsReserved_NamesThatLoadCodeOrRewireAShell_AreReserved(string name)
    {
        Assert.True(ReservedVariableNames.IsReserved(name));
    }

    [Theory]
    [InlineData("DB_PASSWORD")]
    [InlineData("API_KEY")]
    [InlineData("PATH_TO_CERT")]
    [InlineData("MYPATH")]
    [InlineData("LDAP_PASSWORD")]
    [InlineData("LD")]
    [InlineData("ENVIRONMENT")]
    [InlineData("PS")]
    [InlineData("DYLD")]
    public void IsReserved_OrdinaryNames_AreNot(string name)
    {
        Assert.False(ReservedVariableNames.IsReserved(name));
    }
}
