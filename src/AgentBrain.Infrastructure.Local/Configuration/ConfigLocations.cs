using AgentBrain.Application.Configuration;

namespace AgentBrain.Infrastructure.Local.Configuration;

/// <summary>The parts of the process environment configuration discovery depends on. Injected so discovery is testable.</summary>
public sealed record HostEnvironment(Func<string, string?> GetVariable, bool IsWindows, string HomeDirectory)
{
    public static HostEnvironment Current { get; } = new(
        Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

/// <summary>File names and discovery rules for configuration (plan §5.1, §7.4).</summary>
public static class ConfigLocations
{
    public const string ProjectDirectoryName = ".agent-brain";
    public const string ProjectFileName = "config.json";
    public const string LocalFileName = "config.local.json";
    public const string UserDirectoryName = "agent-brain";
    public const string UserFileName = "config.json";

    public const string ProjectVariable = "AGENT_BRAIN_PROJECT";
    public const string ConfigDirVariable = "AGENT_BRAIN_CONFIG_DIR";

    /// <summary>
    /// Finds the project: an explicit directory, then <c>AGENT_BRAIN_PROJECT</c>, then the nearest ancestor
    /// of the working directory containing <c>.agent-brain/config.json</c>. Explicit choices must exist.
    /// </summary>
    public static ProjectLocation? FindProject(string workingDirectory, string? explicitDirectory, HostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var chosen = explicitDirectory ?? NonEmpty(environment.GetVariable(ProjectVariable));
        if (chosen is not null)
        {
            var root = Path.GetFullPath(chosen, workingDirectory);
            return HasProjectFile(root) ? Location(root) : null;
        }

        for (var dir = new DirectoryInfo(Path.GetFullPath(workingDirectory)); dir is not null; dir = dir.Parent)
        {
            if (HasProjectFile(dir.FullName))
            {
                return Location(dir.FullName);
            }
        }

        return null;
    }

    /// <summary>The user configuration directory: <c>AGENT_BRAIN_CONFIG_DIR</c>, else <c>%APPDATA%\agent-brain</c> or <c>$XDG_CONFIG_HOME/agent-brain</c> (default <c>~/.config/agent-brain</c>).</summary>
    public static string UserDirectory(HostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (NonEmpty(environment.GetVariable(ConfigDirVariable)) is { } overridden)
        {
            return Path.GetFullPath(overridden);
        }

        var baseDirectory = environment.IsWindows
            ? NonEmpty(environment.GetVariable("APPDATA")) ?? Path.Combine(environment.HomeDirectory, "AppData", "Roaming")
            : NonEmpty(environment.GetVariable("XDG_CONFIG_HOME")) ?? Path.Combine(environment.HomeDirectory, ".config");
        return Path.Combine(baseDirectory, UserDirectoryName);
    }

    private static bool HasProjectFile(string root) => File.Exists(Path.Combine(root, ProjectDirectoryName, ProjectFileName));

    private static ProjectLocation Location(string root) => new(root, Path.Combine(root, ProjectDirectoryName));

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
