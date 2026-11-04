using System.CommandLine;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Mcp;

namespace AgentBrain.Cli.Commands;

/// <summary><c>brain mcp</c>: the MCP stdio server for agent hosts (A2).</summary>
internal static class McpCommand
{
    public static Command Create(CliIo io)
    {
        var command = new Command("mcp", "Run the MCP server on stdin/stdout for agent hosts (Claude Code, Codex, Gemini CLI, OpenCode).");
        command.SetAction((parse, cancellationToken) => CliContext.RunAsync(parse, io, async (runtime, _) =>
        {
            await McpServerHost.RunAsync(runtime, cancellationToken);
            return ExitCodes.Success;
        }, cancellationToken));
        return command;
    }
}
