using System.CommandLine;

namespace EnvSync.Cli.Commands;

internal static class CommandTree
{
    public static RootCommand Build(CommandContext context, GlobalOptions options)
    {
        var root = new RootCommand("envsync: gives a program its secrets straight from Azure Key Vault, AWS Secrets Manager or HashiCorp Vault, without a plain-text .env file.");

        foreach (var option in options.All)
        {
            root.Options.Add(option);
        }

        root.Subcommands.Add(new RunCommand(context).Build());
        root.Subcommands.Add(new EnvCommand(context).Build());
        root.Subcommands.Add(new CheckCommand(context).Build());
        return root;
    }
}
