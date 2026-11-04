using System.CommandLine;
using AgentBrain.Application.Common;
using AgentBrain.Application.Operations.Relationships;
using AgentBrain.Application.Retrieval;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Cli.Commands;

/// <summary><c>brain get</c>, <c>brain refs</c>, <c>brain backlinks</c> and <c>brain link</c>.</summary>
internal static class ContentCommands
{
    public static Command Get(CliIo io)
    {
        var refs = new Argument<string[]>("refs") { Description = "collection-id/entry-id[#section], one or more.", Arity = ArgumentArity.OneOrMore };
        var maxBytes = new Option<int?>("--max-bytes") { Description = "Byte limit per entry (bounded by configuration)." };
        var withRefs = new Option<bool>("--with-refs") { Description = "Also list the entry's outgoing references." };
        var command = new Command("get", "Fetch entries or sections by identity.") { refs, maxBytes, withRefs };

        command.SetAction((parse, cancellationToken) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var targets = new List<EntryRef>();
            foreach (var text in parse.GetValue(refs) ?? [])
            {
                if (!EntryRef.TryParse(text, out var target))
                {
                    return CliContext.Fail(io, renderer, Error.Validation($"'{text}' is not collection-id/entry-id[#section]."));
                }

                targets.Add(target);
            }

            var result = await runtime.Get.ExecuteAsync(targets, parse.GetValue(maxBytes), parse.GetValue(withRefs), cancellationToken);
            if (!result.IsSuccess)
            {
                return CliContext.Fail(io, renderer, result.Error);
            }

            CliContext.Write(io, renderer.RenderEntries(result.Value.Entries, result.Value.Failures), renderer);
            return result.Value.Failures.Count == 0 ? ExitCodes.Success : ExitCodes.For(result.Value.Failures[0].Error.Code);
        }, cancellationToken));
        return command;
    }

    public static Command References(CliIo io)
    {
        var entry = EntryArgument();
        var command = new Command("refs", "List an entry's outgoing references and whether each resolves.") { entry };
        command.SetAction((parse, cancellationToken) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            if (!EntryRef.TryParse(parse.GetValue(entry), out var target))
            {
                return CliContext.Fail(io, renderer, Error.Validation("Expected collection-id/entry-id."));
            }

            var result = await runtime.Relations.ReferencesAsync(target, cancellationToken);
            return result.IsSuccess ? CliContext.Write(io, renderer.RenderReferences(target, result.Value), renderer) : CliContext.Fail(io, renderer, result.Error);
        }, cancellationToken));
        return command;
    }

    public static Command Backlinks(CliIo io)
    {
        var entry = EntryArgument();
        var cursor = new Option<string?>("--cursor") { Description = "Continue from a previous page." };
        var pageSize = new Option<int>("--page-size") { Description = "Backlinks per page.", DefaultValueFactory = _ => PageDefaults.PageSize };
        var command = new Command("backlinks", "List entries that link to an entry.") { entry, cursor, pageSize };
        command.SetAction((parse, cancellationToken) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            if (!EntryRef.TryParse(parse.GetValue(entry), out var target))
            {
                return CliContext.Fail(io, renderer, Error.Validation("Expected collection-id/entry-id."));
            }

            var result = await runtime.Relations.BacklinksAsync(target, parse.GetValue(cursor), parse.GetValue(pageSize), cancellationToken);
            return result.IsSuccess ? CliContext.Write(io, renderer.RenderBacklinks(target, result.Value), renderer) : CliContext.Fail(io, renderer, result.Error);
        }, cancellationToken));
        return command;
    }

    public static Command Link(CliIo io)
    {
        var entry = EntryArgument();
        var section = new Option<string?>("--section") { Description = "Heading text or anchor of a section." };
        var label = new Option<string?>("--label") { Description = "Link text (default: the reference)." };
        var command = new Command("link", "Print the canonical Markdown link for an entry or section.") { entry, section, label };
        command.SetAction(parse =>
        {
            if (!EntryRef.TryParse(parse.GetValue(entry), out var target))
            {
                io.Error.WriteLine("error validation_failed: expected collection-id/entry-id[#section].");
                return ExitCodes.Usage;
            }

            io.Out.WriteLine(RelationsOperation.CanonicalLink(target, parse.GetValue(section), parse.GetValue(label)).Value);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Argument<string> EntryArgument() => new("ref") { Description = "collection-id/entry-id" };
}
