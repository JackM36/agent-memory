using System.CommandLine;
using AgentBrain.Cli.Commands;
using AgentBrain.Cli.Infrastructure;

namespace AgentBrain.Cli;

/// <summary>Builds the <c>brain</c> command tree (plan §7.1). Commands are added per phase.</summary>
internal static class BrainCommandLine
{
    public static RootCommand Create() => Create(CliIo.Console);

    public static RootCommand Create(CliIo io)
    {
        var root = new RootCommand("Agent Brain: project and cross-project knowledge for coding agents and humans.");
        foreach (var option in GlobalOptions.All)
        {
            root.Options.Add(option);
        }

        root.Subcommands.Add(DiscoveryCommands.Search(io));
        root.Subcommands.Add(DiscoveryCommands.List(io));
        root.Subcommands.Add(ContentCommands.Get(io));
        root.Subcommands.Add(ContentCommands.References(io));
        root.Subcommands.Add(ContentCommands.Backlinks(io));
        root.Subcommands.Add(ContentCommands.Link(io));
        root.Subcommands.Add(DiscoveryCommands.Collections(io));
        root.Subcommands.Add(MaintenanceCommands.Index(io));
        root.Subcommands.Add(MaintenanceCommands.Config(io));
        foreach (var command in WriteCommands.All(io))
        {
            root.Subcommands.Add(command);
        }

        root.Subcommands.Add(ConnectionCommands.Create(io));
        root.Subcommands.Add(CollectionCommands.Create(io));
        root.Subcommands.Add(SetupCommands.Init(io));
        root.Subcommands.Add(SetupCommands.Doctor(io));
        root.Subcommands.Add(SetupCommands.Hook(io));
        root.Subcommands.Add(McpCommand.Create(io));
        return root;
    }
}
