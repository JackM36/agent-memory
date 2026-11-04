namespace AgentBrain.Application.Configuration;

/// <summary>Configuration levels, lowest precedence first (plan §7.4).</summary>
public enum ConfigLevel
{
    BuiltIn,
    User,
    Project,
    ProjectLocal,
    Invocation,
}

/// <summary>The project an invocation runs in: its root and its <c>.agent-brain</c> directory.</summary>
public sealed record ProjectLocation(string RootDirectory, string ConfigDirectory);

/// <summary>
/// A loaded document. Relative paths in it resolve against <see cref="BaseDirectory"/> — the directory
/// of the declaring file, or the working directory for per-call overrides.
/// </summary>
public sealed record LoadedConfigDocument(ConfigLevel Level, string? FilePath, string BaseDirectory, ConfigDocument Document);

/// <summary>Where configuration should be discovered from, plus per-call overrides.</summary>
public sealed record ConfigurationRequest(string WorkingDirectory, string? ExplicitProjectDirectory = null, ConfigDocument? Invocation = null);

/// <summary>Everything layering needs: the project (if any) and every applicable document.</summary>
public sealed record ConfigurationInput(ProjectLocation? Project, IReadOnlyList<LoadedConfigDocument> Documents, string? WorkingDirectory = null);

/// <summary>Which level and file set an effective value.</summary>
public sealed record ConfigOrigin(ConfigLevel Level, string? FilePath)
{
    public override string ToString() => FilePath is null ? Level.ToString() : $"{Level} ({FilePath})";
}
