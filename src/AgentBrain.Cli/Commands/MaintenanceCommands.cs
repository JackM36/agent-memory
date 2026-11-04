using System.CommandLine;
using System.Globalization;
using System.Text;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Indexing;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Composition;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Cli.Commands;

/// <summary><c>brain index …</c> and <c>brain config …</c>.</summary>
internal static class MaintenanceCommands
{
    public static Command Index(CliIo io)
    {
        var command = new Command("index", "Inspect and maintain the local search index.");
        command.Subcommands.Add(IndexSubcommand(io, "status", "Show index size, diagnostics and the last refresh.", null));
        command.Subcommands.Add(IndexSubcommand(io, "reindex", "Refresh changed files now.", SyncMode.Incremental));
        command.Subcommands.Add(IndexSubcommand(io, "verify", "Hash every file to catch changes size and time checks missed.", SyncMode.Verify));
        command.Subcommands.Add(IndexSubcommand(io, "reset", "Discard the index and rebuild it from the collections.", SyncMode.Rebuild));
        return command;
    }

    public static Command Config(CliIo io)
    {
        var origin = new Option<bool>("--origin") { Description = "Show which file set each value." };
        var show = new Command("show", "Print the effective configuration.") { origin };
        show.SetAction(async (parse, cancellationToken) =>
        {
            var resolution = await BrainRuntime.ResolveConfigurationAsync(CliContext.Options(parse, io), cancellationToken);
            if (!resolution.IsSuccess)
            {
                return CliContext.Fail(io, CliContext.Renderer(parse, io), resolution.Error);
            }

            await io.Out.WriteAsync(Describe(resolution.Value, parse.GetValue(origin)));
            return resolution.Value.HasErrors ? ExitCodes.Usage : ExitCodes.Success;
        });

        var validate = new Command("validate", "Check configuration and report problems.");
        validate.SetAction(async (parse, cancellationToken) =>
        {
            var resolution = await BrainRuntime.ResolveConfigurationAsync(CliContext.Options(parse, io), cancellationToken);
            if (!resolution.IsSuccess)
            {
                return CliContext.Fail(io, CliContext.Renderer(parse, io), resolution.Error);
            }

            foreach (var diagnostic in resolution.Value.Diagnostics)
            {
                await io.Out.WriteLineAsync(diagnostic.ToString());
            }

            await io.Out.WriteLineAsync(resolution.Value.HasErrors ? "configuration has errors" : "configuration is valid");
            return resolution.Value.HasErrors ? ExitCodes.Usage : ExitCodes.Success;
        });

        return new Command("config", "Inspect configuration.") { show, validate };
    }

    private static Command IndexSubcommand(CliIo io, string name, string description, SyncMode? mode)
    {
        var command = new Command(name, description);
        command.SetAction((parse, cancellationToken) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var report = mode is { } m
                ? await runtime.Index.SynchronizeAsync(m, cancellationToken)
                : await runtime.Index.StatusAsync(cancellationToken);
            return report.IsSuccess ? CliContext.Write(io, renderer.RenderIndex(report.Value), renderer) : CliContext.Fail(io, renderer, report.Error);
        }, cancellationToken));
        return command;
    }

    private static string Describe(ConfigResolution resolution, bool withOrigin)
    {
        var c = resolution.Configuration;
        var text = new StringBuilder();

        void Line(string key, string? value, string? originKey = null)
        {
            if (value is null)
            {
                return;
            }

            text.Append(key).Append(" = ").Append(value);
            if (withOrigin && c.Origins.TryGetValue(originKey ?? key, out var origin))
            {
                text.Append("    [").Append(origin).Append(']');
            }

            text.Append('\n');
        }

        static string Join(IEnumerable<string> values) => "[" + string.Join(", ", values) + "]";
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        if (c.Project is { } project)
        {
            Line("project.id", project.Id);
            Line("project.root", project.RootDirectory, "project.id");
            Line("project.technologies", Join(project.Technologies));
            Line("project.packages", Join(project.Packages.Select(p => $"{p.Key}@{p.Value}")));
        }
        else
        {
            text.Append("project = (none: only user-level sources apply)\n");
        }

        foreach (var collection in c.Collections)
        {
            Line($"collections.{collection.Id}", $"{collection.Path} {Join(Capabilities.ToNames(collection.Capabilities))}{(collection.ImmediatePublish ? " immediatePublish" : "")}");
        }

        foreach (var connection in c.Connections)
        {
            var f = connection.Forward;
            var forward = string.Join(',', new[] { ("project", f.Project), ("technologies", f.Technologies), ("files", f.Files), ("symbols", f.Symbols) }.Where(x => x.Item2).Select(x => x.Item1));
            Line($"connections.{connection.Name}", $"{connection.Url} key from ${connection.Credential.EnvironmentVariable} forward [{forward}] timeout {connection.Timeout.TotalMilliseconds}ms");
        }

        Line("writes.defaultCollection", c.DefaultWriteCollection?.Value ?? "(none)");
        Line("sourcePreference", Join(c.SourcePreference));
        Line("search.relaxation", c.Search.Relaxation.ToString());
        Line("search.maxResults", Number(c.Search.MaxResults));
        Line("search.maxTokens", Number(c.Search.MaxTokens));
        Line("search.maxBytes", Number(c.Search.MaxBytes));
        Line("search.candidatesPerSource", Number(c.Search.CandidatesPerSource));
        Line("search.kinds", Join(c.Search.Kinds.Select(k => WireNames.Kinds.Name(k))));
        Line("search.log", c.Search.Log ? "true" : "false");
        Line("get.maxBytes", Number(c.Get.MaxBytes));
        Line("get.batchTargets", Number(c.Get.BatchTargets));
        Line("get.batchBytes", Number(c.Get.BatchBytes));
        foreach (var group in c.Aliases)
        {
            Line($"aliases.{group.Terms[0]}", Join(group.Terms.Skip(1)));
        }

        Line("runtimeDir", c.RuntimeDirectory);
        Line("proposals", c.ProposalsDirectory);

        foreach (var diagnostic in resolution.Diagnostics)
        {
            text.Append(diagnostic.Severity == IssueSeverity.Error ? "error " : "warning ").Append(diagnostic.Path).Append(": ").Append(diagnostic.Message).Append('\n');
        }

        return text.ToString();
    }
}
