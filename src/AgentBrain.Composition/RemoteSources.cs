using AgentBrain.Application.Abstractions.Providers;
using AgentBrain.Application.Configuration;
using AgentBrain.Domain.Validation;
using AgentBrain.Infrastructure.Http;
using AgentBrain.Infrastructure.Local.Configuration;
using AgentBrain.Infrastructure.Local.Security;

namespace AgentBrain.Composition;

/// <summary>
/// One <see cref="RemoteHttpProvider"/> per configured connection, with its key from the named environment
/// variable. A connection whose key is not set is skipped with a warning rather than failing every search.
/// </summary>
internal static class RemoteSources
{
    public static IEnumerable<(IKnowledgeProvider, TimeSpan?)> Create(
        EffectiveConfiguration configuration, HostEnvironment environment, List<IDisposable> resources, List<ConfigDiagnostic> diagnostics)
    {
        var credentials = new EnvironmentCredentialResolver(environment);
        foreach (var connection in configuration.Connections)
        {
            var key = credentials.Resolve(connection.Credential);
            if (key is null)
            {
                diagnostics.Add(new ConfigDiagnostic(IssueSeverity.Warning, $"connections.{connection.Name}",
                    $"Skipped: set the environment variable {connection.Credential.EnvironmentVariable} to this connection's API key.", null));
                continue;
            }

            var provider = new RemoteHttpProvider(connection, key, configuration.Project?.Id);
            resources.Add(provider);
            yield return (provider, connection.Timeout);
        }
    }
}
