using System.CommandLine;
using AgentBrain.Application.Common;
using AgentBrain.Application.Writing;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Cli.Commands;

/// <summary>The options that describe entry content, shared by new, update, propose and publish.</summary>
internal sealed class EntryOptions
{
    public Option<string?> Title { get; } = new("--title") { Description = "Title (single line, at most 120 characters)." };

    public Option<string?> Summary { get; } = new("--summary") { Description = "Summary shown in search results (single line, at most 240 characters)." };

    public Option<string?> Body { get; } = new("--body") { Description = "Markdown body text." };

    public Option<string?> BodyFile { get; } = new("--body-file") { Description = "Read the Markdown body from a file, or '-' for stdin." };

    public Option<string[]> Tags { get; } = DiscoveryCommands.Many("--tag", "A tag. Repeatable; on update, replaces all tags.");

    public Option<string[]> Files { get; } = DiscoveryCommands.Many("--file", "A project-relative file glob this entry is about. Repeatable.");

    public Option<string[]> Symbols { get; } = DiscoveryCommands.Many("--symbol", "A code symbol this entry is about. Repeatable.");

    public Option<string[]> Evidence { get; } = DiscoveryCommands.Many("--evidence", "Evidence: a path, a test, or 'user confirmed <date>'. Repeatable.");

    public Option<string[]> AllowSecretRules { get; } = DiscoveryCommands.Many("--allow-secret-rule", "Allow one secret-scan rule for this write, only after the user confirmed a false positive.");

    public IEnumerable<Option> Content => [Title, Summary, Body, BodyFile, Tags, Files, Symbols, Evidence];

    public async Task<Result<string?>> ReadBodyAsync(ParseResult parse, CliIo io)
    {
        var file = parse.GetValue(BodyFile);
        var text = parse.GetValue(Body);
        if (file is not null && text is not null)
        {
            return Error.Validation("Use --body or --body-file, not both.");
        }

        if (file is null)
        {
            return text;
        }

        try
        {
            return file == "-" ? await io.In.ReadToEndAsync() : await File.ReadAllTextAsync(Path.GetFullPath(file, io.WorkingDirectory));
        }
        catch (IOException ex)
        {
            return Error.Validation($"Cannot read the body file: {ex.Message}");
        }
    }

    public async Task<Result<NewEntry>> NewEntryAsync(ParseResult parse, CliIo io, EntryKind kind, bool blocking, EntryId? id)
    {
        var title = parse.GetValue(Title);
        var summary = parse.GetValue(Summary);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
        {
            return Error.Validation("A new entry needs --title and --summary.");
        }

        var body = await ReadBodyAsync(parse, io);
        if (!body.IsSuccess)
        {
            return body.Error;
        }

        return new NewEntry(kind, title, summary, body.Value ?? "")
        {
            Id = id,
            Tags = parse.GetValue(Tags) ?? [],
            Files = parse.GetValue(Files) ?? [],
            Symbols = parse.GetValue(Symbols) ?? [],
            Evidence = parse.GetValue(Evidence) ?? [],
            Blocking = blocking,
        };
    }

    public async Task<Result<EntryPatch>> PatchAsync(ParseResult parse, CliIo io)
    {
        var body = await ReadBodyAsync(parse, io);
        if (!body.IsSuccess)
        {
            return body.Error;
        }

        static IReadOnlyList<string>? NullIfEmpty(string[]? values) => values is { Length: > 0 } ? values : null;
        return new EntryPatch
        {
            Title = parse.GetValue(Title),
            Summary = parse.GetValue(Summary),
            Body = body.Value,
            Tags = NullIfEmpty(parse.GetValue(Tags)),
            Files = NullIfEmpty(parse.GetValue(Files)),
            Symbols = NullIfEmpty(parse.GetValue(Symbols)),
            Evidence = NullIfEmpty(parse.GetValue(Evidence)),
        };
    }

    public static Result<EntryRef> ParseRef(string? text) =>
        EntryRef.TryParse(text, out var reference) ? reference.WithoutAnchor() : Error.Validation($"'{text}' is not collection-id/entry-id.");

    public static Result<Revision> ParseRevision(string? text) =>
        Revision.TryParse(text, out var revision) ? revision : Error.Validation($"'{text}' is not a revision (r1-…). Get it from brain get or brain list --format json.");
}
