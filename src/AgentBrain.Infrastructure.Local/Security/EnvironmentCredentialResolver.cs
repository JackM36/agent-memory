using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Infrastructure.Local.Configuration;

namespace AgentBrain.Infrastructure.Local.Security;

/// <summary>Resolves credentials from environment variables named in configuration (spec §11).</summary>
public sealed class EnvironmentCredentialResolver(HostEnvironment environment) : ICredentialResolver
{
    public string? Resolve(CredentialReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var value = environment.GetVariable(reference.EnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
