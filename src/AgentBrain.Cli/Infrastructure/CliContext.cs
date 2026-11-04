using System.CommandLine;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Composition;
using AgentBrain.Contracts.Rendering;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Cli.Infrastructure;

/// <summary>Where command output goes. Program output goes to <see cref="Out"/>; diagnostics only ever to <see cref="Error"/>.</summary>
internal sealed record CliIo(TextWriter Out, TextWriter Error, bool OutputIsTerminal, string WorkingDirectory)
{
    public static CliIo Console { get; } = new(System.Console.Out, System.Console.Error, !System.Console.IsOutputRedirected, Directory.GetCurrentDirectory()) { In = System.Console.In };

    /// <summary>Input for <c>--body-file -</c>.</summary>
    public TextReader In { get; init; } = TextReader.Null;
}

/// <summary>Options every command accepts.</summary>
internal static class GlobalOptions
{
    public static readonly Option<string?> Project = new("--project")
    {
        Description = "Project directory (default: AGENT_BRAIN_PROJECT, then the nearest .agent-brain/config.json above the working directory).",
        Recursive = true,
    };

    public static readonly Option<string?> Format = new("--format")
    {
        Description = "Output format: compact, human or json (default: human on a terminal, compact otherwise).",
        Recursive = true,
    };

    public static readonly Option<string[]> CollectionRoots = new("--collection-root")
    {
        Description = "Register a read-only Markdown collection directory for this call only. Repeatable.",
        Recursive = true,
        AllowMultipleArgumentsPerToken = false,
    };

    static GlobalOptions() => Format.AcceptOnlyFromAmong(TextRenderer.CompactName, TextRenderer.HumanName, JsonRenderer.Name);

    public static IEnumerable<Option> All => [Project, Format, CollectionRoots];
}

/// <summary>Shared command plumbing: renderer choice, runtime opening, error reporting and exit codes (plan §7.1).</summary>
internal static class CliContext
{
    public static IResponseRenderer Renderer(ParseResult parse, CliIo io) => parse.GetValue(GlobalOptions.Format) switch
    {
        JsonRenderer.Name => new JsonRenderer(),
        TextRenderer.CompactName => TextRenderer.Compact,
        TextRenderer.HumanName => TextRenderer.Human,
        _ => io.OutputIsTerminal ? TextRenderer.Human : TextRenderer.Compact,
    };

    public static RuntimeOptions Options(ParseResult parse, CliIo io, ConfigDocument? invocation = null) => new(io.WorkingDirectory)
    {
        ProjectDirectory = parse.GetValue(GlobalOptions.Project),
        ExtraCollectionRoots = parse.GetValue(GlobalOptions.CollectionRoots) ?? [],
        Invocation = invocation,
    };

    /// <summary>Opens the runtime, prints configuration warnings to stderr, and runs <paramref name="action"/>.</summary>
    public static async Task<int> RunAsync(ParseResult parse, CliIo io, Func<BrainRuntime, IResponseRenderer, Task<int>> action, CancellationToken cancellationToken)
    {
        var renderer = Renderer(parse, io);
        var opened = await BrainRuntime.OpenAsync(Options(parse, io), cancellationToken);
        if (!opened.IsSuccess)
        {
            return Fail(io, renderer, opened.Error);
        }

        using var runtime = opened.Value;
        foreach (var diagnostic in runtime.Diagnostics.Where(d => d.Severity == IssueSeverity.Warning))
        {
            await io.Error.WriteLineAsync($"warning {diagnostic.Path}: {diagnostic.Message}");
        }

        return await action(runtime, renderer);
    }

    /// <summary>Errors go to stdout in the chosen format (callers parse them) and are summarised on stderr.</summary>
    public static int Fail(CliIo io, IResponseRenderer renderer, Error error)
    {
        io.Out.Write(renderer.RenderError(error));
        if (renderer is JsonRenderer)
        {
            io.Out.WriteLine();
        }

        return ExitCodes.For(error.Code);
    }

    public static int Write(CliIo io, string text, IResponseRenderer renderer)
    {
        io.Out.Write(text);
        if (renderer is JsonRenderer)
        {
            io.Out.WriteLine();
        }

        return ExitCodes.Success;
    }
}

/// <summary>Process exit codes (plan §7.1).</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Internal = 1;
    public const int Usage = 2;
    public const int NotFound = 3;
    public const int Conflict = 4;
    public const int Denied = 5;
    public const int Unavailable = 6;
    public const int Budget = 7;
    public const int Secret = 8;

    public static int For(ErrorCode code) => code switch
    {
        ErrorCode.ValidationFailed or ErrorCode.ConfigInvalid or ErrorCode.ProtocolVersionUnsupported => Usage,
        ErrorCode.NotFound or ErrorCode.SectionNotFound => NotFound,
        ErrorCode.RevisionConflict or ErrorCode.LockTimeout => Conflict,
        ErrorCode.Unauthorized or ErrorCode.Forbidden or ErrorCode.ReadOnly or ErrorCode.UnsupportedOperation => Denied,
        ErrorCode.ProviderUnavailable or ErrorCode.IndexUnavailable => Unavailable,
        ErrorCode.BudgetTooSmall => Budget,
        ErrorCode.SecretDetected => Secret,
        _ => Internal,
    };
}
