using AgentBrain.Application.Common;
using AgentBrain.Infrastructure.Http;
using AgentBrain.Infrastructure.Local.Configuration;
using AgentBrain.Infrastructure.Local.Security;

namespace AgentBrain.Composition;

/// <summary><c>brain connection test</c>: authenticates against a configured remote and lists what the key can read.</summary>
public static class ConnectionTester
{
    public static async ValueTask<Result<IReadOnlyList<string>>> TestAsync(RuntimeOptions options, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resolution = await BrainRuntime.ResolveConfigurationAsync(options, cancellationToken);
        if (!resolution.IsSuccess)
        {
            return resolution.Error;
        }

        var connection = resolution.Value.Configuration.Connections.FirstOrDefault(c => c.Name == name);
        if (connection is null)
        {
            return Error.NotFound($"No connection named '{name}' is configured.");
        }

        var key = new EnvironmentCredentialResolver(options.Environment).Resolve(connection.Credential);
        if (key is null)
        {
            return new Error(ErrorCode.Unauthorized, $"Set the environment variable {connection.Credential.EnvironmentVariable} to the API key for '{name}'.");
        }

        using var provider = new RemoteHttpProvider(connection, key, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connection.Timeout * 3);
        try
        {
            var collections = await provider.DescribeAsync(timeout.Token);
            return collections.IsSuccess
                ? Result<IReadOnlyList<string>>.Success([.. collections.Value.Select(c => $"{c.Id} [{string.Join(',', c.Capabilities)}] {c.Title}")])
                : collections.Error;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Error(ErrorCode.ProviderUnavailable, $"'{name}' did not answer within {(connection.Timeout * 3).TotalSeconds:0} seconds.");
        }
    }
}
