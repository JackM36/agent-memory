using System.CommandLine;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Cli.Update;
using AgentBrain.Integrations;

namespace AgentBrain.Cli.Commands;

/// <summary><c>brain upgrade</c>, and the version line <c>brain doctor</c> adds.</summary>
internal static class UpgradeCommand
{
    public const string TokenVariable = "AGENT_BRAIN_GITHUB_TOKEN";

    public static Command Create(CliIo io)
    {
        var check = new Option<bool>("--check") { Description = "Only report whether a newer release exists." };
        var version = new Option<string?>("--version") { Description = "Install this version (e.g. 0.3.1) instead of the latest; also downgrades." };
        var force = new Option<bool>("--force") { Description = "Reinstall even when already up to date." };
        var command = new Command("upgrade", "Upgrade brain in place to the latest GitHub release (checksum-verified).") { check, version, force };
        command.SetAction(async (parse, ct) =>
        {
            ReleaseVersion? wanted = null;
            if (parse.GetValue(version) is { } text && !ReleaseVersion.TryParse(text, out wanted))
            {
                await io.Error.WriteLineAsync($"error validation_failed: '{text}' is not a version such as 0.3.1.");
                return ExitCodes.Usage;
            }

            var updater = Updater();
            var current = ReleaseUpdater.CurrentVersion;
            var release = await updater.FindAsync(wanted, ct);
            if (!release.IsSuccess)
            {
                await io.Error.WriteLineAsync($"error {Application.Common.ErrorCodes.Names.Name(release.Error.Code)}: {release.Error.Message}");
                return ExitCodes.For(release.Error.Code);
            }

            var latest = release.Value.Version;
            if (parse.GetValue(check))
            {
                await io.Out.WriteLineAsync(latest > current
                    ? $"brain {current}; {latest} is available — run brain upgrade"
                    : $"brain {current} is up to date (latest release {latest})");
                return ExitCodes.Success;
            }

            if (wanted is null && latest <= current && !parse.GetValue(force))
            {
                await io.Out.WriteLineAsync($"brain {current} is up to date.");
                return ExitCodes.Success;
            }

            if (InstallDirectory() is not { } directory)
            {
                await io.Error.WriteLineAsync("error unsupported_operation: brain upgrade replaces the installed brain executable; this process is not one (run it from an installed brain, or use the install script).");
                return ExitCodes.Denied;
            }

            if (ReleaseUpdater.AssetName() is not { } asset)
            {
                await io.Error.WriteLineAsync("error unsupported_operation: no release is published for this platform; build from source.");
                return ExitCodes.Denied;
            }

            var installed = await updater.InstallAsync(release.Value, asset, directory, ct);
            if (!installed.IsSuccess)
            {
                await io.Error.WriteLineAsync($"error {Application.Common.ErrorCodes.Names.Name(installed.Error.Code)}: {installed.Error.Message}");
                return ExitCodes.For(installed.Error.Code);
            }

            await io.Out.WriteLineAsync($"updated brain {current} → {latest} in {directory}");
            await io.Out.WriteLineAsync("Restart agent sessions to use the new MCP server. In each project, brain doctor reports whether its integration files need brain init.");
            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>The <c>version</c> check for <c>brain doctor</c>: never fails the doctor, and is skipped with AGENT_BRAIN_NO_UPDATE_CHECK.</summary>
    public static async Task<Check> DoctorCheckAsync(CancellationToken cancellationToken)
    {
        var current = ReleaseUpdater.CurrentVersion;
        if (Environment.GetEnvironmentVariable(ReleaseUpdater.NoCheckVariable) is { Length: > 0 })
        {
            return new Check("version", CheckState.Ok, $"brain {current} (update check disabled)");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var latest = await Updater().FindAsync(null, timeout.Token);
            return !latest.IsSuccess
                ? new Check("version", CheckState.Ok, $"brain {current} (could not check for updates: {latest.Error.Message})")
                : latest.Value.Version > current
                    ? new Check("version", CheckState.Warning, $"brain {current}; {latest.Value.Version} is available — run brain upgrade")
                    : new Check("version", CheckState.Ok, $"brain {current} is up to date");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Check("version", CheckState.Ok, $"brain {current} (update check timed out)");
        }
    }

    private static ReleaseUpdater Updater() =>
        new(ReleaseUpdater.DefaultRepository, token: Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } t ? t : null);

    /// <summary>The folder of the running brain executable; null when running under <c>dotnet</c> (a development build).</summary>
    private static string? InstallDirectory()
    {
        var process = Environment.ProcessPath;
        var name = Path.GetFileNameWithoutExtension(process);
        return process is not null && string.Equals(name, "brain", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(process) : null;
    }
}
