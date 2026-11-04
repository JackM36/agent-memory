using System.Net;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Configuration;
using AgentBrain.Composition;
using Microsoft.AspNetCore.HttpOverrides;

namespace AgentBrain.Server;

/// <summary>
/// Builds the <c>brain-server</c> web application (plan §9.6, §10). Endpoints only bind requests, authenticate
/// and map errors; every rule lives in the application operations shared with the CLI.
/// </summary>
public static class ServerApp
{
    public const long MaxRequestBytes = 1024 * 1024;

    public static WebApplication Build(HostRuntime runtime, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var host = runtime.Host;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(WatcherOptions.Default);
        builder.Services.AddHostedService<MarkdownWatcher>();

        var listen = new Uri(host.Listen);
        if (host.Transport == HostTransport.Local && !listen.IsLoopback)
        {
            throw new InvalidOperationException($"transport local must listen on a loopback address, not {listen.Host}.");
        }

        if (host.Transport == HostTransport.Tls)
        {
            if (listen.Scheme != Uri.UriSchemeHttps && !host.AllowInsecureHttp)
            {
                throw new InvalidOperationException("transport tls needs an https listen address (or allowInsecureHttp).");
            }

            var password = host.CertificatePasswordEnv is null ? null : Environment.GetEnvironmentVariable(host.CertificatePasswordEnv);
            builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h =>
                h.ServerCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(host.CertificatePath!, password)));
        }

        builder.WebHost.UseUrls(host.Listen);

        // Requests are small JSON documents; the largest legitimate one is a publish batch.
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxRequestBytes);
        configure?.Invoke(builder);

        var app = builder.Build();
        if (host.Transport == HostTransport.BehindProxy)
        {
            var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            foreach (var network in host.TrustedProxies)
            {
                forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }

            app.UseForwardedHeaders(forwarded);
            app.Use(async (context, next) =>
            {
                // Behind a proxy, every API request must have arrived over HTTPS (X-Forwarded-Proto from a trusted proxy).
                if (!context.Request.IsHttps && context.Request.Path.StartsWithSegments("/v1", StringComparison.Ordinal) && !host.AllowInsecureHttp)
                {
                    await Http.WriteErrorAsync(context, new(Application.Common.ErrorCode.Forbidden, "HTTPS is required; connect through the TLS proxy."));
                    return;
                }

                await next(context);
            });
        }

        app.Use(Http.AuthenticateAsync);
        Endpoints.Map(app);
        return app;
    }

    /// <summary>The address <c>brain-server health</c> probes.</summary>
    public static Uri HealthUrl(HostConfiguration host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var listen = new Uri(host.Listen);
        var hostname = listen.Host is "0.0.0.0" or "[::]" or "+" or "*" ? IPAddress.Loopback.ToString() : listen.Host;
        return new UriBuilder(listen.Scheme, hostname, listen.Port, "/health").Uri;
    }

    /// <summary>Scopes for the keys <c>brain-server init</c> creates.</summary>
    public static readonly IReadOnlyList<string> AdminScopes =
        [ApiKeyService.AdminScope, "collection:*:read", "collection:*:propose", "collection:*:maintain", "collection:*:publish"];

    public static readonly IReadOnlyList<string> ReadWriteScopes = ["collection:*:read", "collection:*:propose", "collection:*:maintain"];
}
