using System.Text.Json.Serialization;
using AgentBrain.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace AgentBrain.Mcp;

/// <summary>
/// Runs the MCP stdio server (<c>brain mcp</c>). stdout carries only protocol frames; every log line goes
/// to stderr (plan §2.1).
/// </summary>
public static class McpServerHost
{
    public const string ServerName = "agent-brain";

    public static async Task RunAsync(BrainRuntime runtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(runtime);
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = ServerName, Version = typeof(McpServerHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" })
            .WithStdioServerTransport()
            .WithTools<BrainTools>(McpJsonContext.Default.Options)
            .WithTools<BrainWriteTools>(McpJsonContext.Default.Options)
            .WithPrompts<BrainPrompts>(McpJsonContext.Default.Options);
        builder.Services.PostConfigure<McpServerOptions>(ToolSchemaCompactor.Compact);
        await builder.Build().RunAsync(cancellationToken);
    }
}

/// <summary>Source-generated serialization for tool parameters and results (AOT-safe; verified in ADR 0001).</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
