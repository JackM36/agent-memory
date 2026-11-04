using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;

namespace AgentBrain.Infrastructure.Local.Configuration;

/// <summary>Loads the user, project and project-local configuration files for one invocation.</summary>
public sealed class ConfigFileSource(HostEnvironment environment) : IConfigurationSource
{
    public async ValueTask<Result<ConfigurationInput>> LoadAsync(ConfigurationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = ConfigLocations.FindProject(request.WorkingDirectory, request.ExplicitProjectDirectory, environment);
        if (project is null && request.ExplicitProjectDirectory is not null)
        {
            return new Error(ErrorCode.ConfigInvalid,
                $"No {ConfigLocations.ProjectDirectoryName}/{ConfigLocations.ProjectFileName} found in '{request.ExplicitProjectDirectory}'. Run 'brain init' there first.");
        }

        var documents = new List<LoadedConfigDocument>();
        var userDirectory = ConfigLocations.UserDirectory(environment);
        var candidates = new List<(ConfigLevel Level, string Path, bool Required)>
        {
            (ConfigLevel.User, Path.Combine(userDirectory, ConfigLocations.UserFileName), false),
        };
        if (project is not null)
        {
            candidates.Add((ConfigLevel.Project, Path.Combine(project.ConfigDirectory, ConfigLocations.ProjectFileName), true));
            candidates.Add((ConfigLevel.ProjectLocal, Path.Combine(project.ConfigDirectory, ConfigLocations.LocalFileName), false));
        }

        foreach (var (level, path, required) in candidates)
        {
            if (!required && !File.Exists(path))
            {
                continue;
            }

            var document = await ConfigFileReader.ReadAsync(path, cancellationToken);
            if (!document.IsSuccess)
            {
                return document.Error;
            }

            documents.Add(new LoadedConfigDocument(level, path, Path.GetDirectoryName(path)!, document.Value));
        }

        if (request.Invocation is { } invocation)
        {
            documents.Add(new LoadedConfigDocument(ConfigLevel.Invocation, null, Path.GetFullPath(request.WorkingDirectory), invocation));
        }

        return new ConfigurationInput(project, documents, Path.GetFullPath(request.WorkingDirectory));
    }
}
