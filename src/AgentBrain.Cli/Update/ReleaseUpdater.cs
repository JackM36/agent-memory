using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBrain.Application.Common;

namespace AgentBrain.Cli.Update;

/// <summary>A published release: its version and the download URL of each asset by name.</summary>
public sealed record ReleaseInfo(ReleaseVersion Version, string Tag, IReadOnlyDictionary<string, Uri> Assets);

/// <summary>
/// <c>brain upgrade</c>: finds the latest (or a given) GitHub release, downloads this platform's archive, checks it
/// against <c>SHA256SUMS</c> and replaces the installed files in place. The install folder stays the same, so MCP
/// registrations and hooks that call <c>brain</c> keep working. A file in use (a running MCP server on Windows)
/// cannot be overwritten but can be renamed, so each old file is moved aside as <c>.old</c> and removed on the next
/// update; if any step fails, the files already swapped are put back.
/// </summary>
public sealed class ReleaseUpdater(string repository, HttpMessageHandler? handler = null, string? token = null)
{
    public const string RepositoryVariable = "AGENT_BRAIN_REPOSITORY";
    public const string NoCheckVariable = "AGENT_BRAIN_NO_UPDATE_CHECK";
    private const string ChecksumsAsset = "SHA256SUMS";

    public string Repository { get; } = repository;

    /// <summary>The version of this binary (local builds are <c>0.0.0-dev</c>).</summary>
    public static ReleaseVersion CurrentVersion
    {
        get
        {
            var informational = typeof(ReleaseUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return ReleaseVersion.TryParse(informational, out var version) ? version : new ReleaseVersion(0, 0, 0, "dev");
        }
    }

    /// <summary>The repository releases come from: <c>AGENT_BRAIN_REPOSITORY</c>, else the one this binary was built from.</summary>
    public static string DefaultRepository =>
        Environment.GetEnvironmentVariable(RepositoryVariable) is { Length: > 0 } overridden
            ? overridden
            : typeof(ReleaseUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "AgentBrainRepository")?.Value
              ?? "JackM36/agent-memory";

    /// <summary>The archive name for this machine, e.g. <c>brain-win-x64.zip</c>; null on unsupported platforms.</summary>
    public static string? AssetName()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null;
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        return os is null || arch is null ? null : $"brain-{os}-{arch}{(os == "win" ? ".zip" : ".tar.gz")}";
    }

    public async Task<Result<ReleaseInfo>> FindAsync(ReleaseVersion? version, CancellationToken cancellationToken)
    {
        var path = version is null ? "releases/latest" : $"releases/tags/v{version}";
        using var client = Client();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.github.com/repos/{Repository}/{path}"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (token is not null)
            {
                // Optional: raises GitHub's API rate limit. Sent only to the API, never with downloads.
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Error.NotFound(version is null ? $"{Repository} has no published release yet." : $"{Repository} has no release v{version}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new Error(ErrorCode.ProviderUnavailable, $"GitHub answered {(int)response.StatusCode} for {Repository} releases.");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!ReleaseVersion.TryParse(tag, out var parsed))
            {
                return new Error(ErrorCode.ProviderUnavailable, $"The release tag '{tag}' is not a version.");
            }

            var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() is { } name && Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url))
                {
                    assets[name] = url;
                }
            }

            return new ReleaseInfo(parsed, tag, assets);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new Error(ErrorCode.ProviderUnavailable, $"Could not read the releases of {Repository}: {ex.Message}");
        }
    }

    /// <summary>Downloads, verifies and installs a release into <paramref name="installDirectory"/>.</summary>
    public async Task<Result> InstallAsync(ReleaseInfo release, string assetName, string installDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!release.Assets.TryGetValue(assetName, out var archiveUrl) || !release.Assets.TryGetValue(ChecksumsAsset, out var checksumsUrl))
        {
            return Error.NotFound($"Release {release.Tag} has no {assetName} (or no {ChecksumsAsset}).");
        }

        var work = Directory.CreateTempSubdirectory("agent-brain-update-").FullName;
        try
        {
            using var client = Client();
            var archive = Path.Combine(work, assetName);
            await DownloadAsync(client, archiveUrl, archive, cancellationToken);
            var checksums = await client.GetStringAsync(checksumsUrl, cancellationToken);

            var expected = ExpectedChecksum(checksums, assetName);
            if (expected is null)
            {
                return Error.Validation($"{ChecksumsAsset} has no entry for {assetName}.");
            }

            string actual;
            await using (var stream = File.OpenRead(archive))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Validation($"Checksum mismatch for {assetName}; nothing was installed.");
            }

            var unpacked = Directory.CreateDirectory(Path.Combine(work, "unpacked")).FullName;
            await ExtractAsync(archive, unpacked, cancellationToken);
            return Replace(unpacked, installDirectory);
        }
        catch (HttpRequestException ex)
        {
            return new Error(ErrorCode.ProviderUnavailable, $"Download failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return new Error(ErrorCode.Internal, $"Could not unpack {assetName}: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // A temporary folder left behind is harmless.
            }
        }
    }

    /// <summary>Removes <c>.old</c> files left by an earlier update (best effort: one still in use stays).</summary>
    public static void RemoveLeftovers(string installDirectory)
    {
        if (!Directory.Exists(installDirectory))
        {
            return;
        }

        foreach (var old in Directory.EnumerateFiles(installDirectory, "*.old"))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still loaded by a running brain; the next update removes it.
            }
        }
    }

    public static string? ExpectedChecksum(string checksums, string assetName)
    {
        ArgumentNullException.ThrowIfNull(checksums);
        foreach (var line in checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == assetName && parts[0].Length == 64)
            {
                return parts[0];
            }
        }

        return null;
    }

    /// <summary>Moves every unpacked file into place, moving the current one aside first; undoes everything on failure.</summary>
    private static Result Replace(string unpacked, string installDirectory)
    {
        Directory.CreateDirectory(installDirectory);
        RemoveLeftovers(installDirectory);
        var swapped = new List<(string Target, string? Aside)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(unpacked))
            {
                var target = Path.Combine(installDirectory, Path.GetFileName(file));
                string? aside = null;
                if (File.Exists(target))
                {
                    aside = AsideName(target);
                    File.Move(target, aside);
                }

                swapped.Add((target, aside));
                File.Move(file, target);
                if (!OperatingSystem.IsWindows() && Path.GetFileName(target) == "brain")
                {
                    File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
            }

            return Result.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (target, aside) in Enumerable.Reverse(swapped))
            {
                try
                {
                    if (File.Exists(target) && aside is not null)
                    {
                        File.Delete(target);
                    }

                    if (aside is not null)
                    {
                        File.Move(aside, target);
                    }
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the .old file remains next to the target and can be renamed back by hand.
                }
            }

            return new Error(ErrorCode.Internal, $"Could not replace the installed files ({ex.Message}); the previous version was restored.");
        }
    }

    private static string AsideName(string target)
    {
        var aside = target + ".old";
        for (var n = 2; File.Exists(aside); n++)
        {
            aside = $"{target}.{n}.old";
        }

        return aside;
    }

    private static async Task ExtractAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ZipFile.ExtractToDirectoryAsync(archive, destination, overwriteFiles: true, cancellationToken);
            return;
        }

        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: true, cancellationToken);
    }

    private static async Task DownloadAsync(HttpClient client, Uri url, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var output = File.Create(path);
        await response.Content.CopyToAsync(output, cancellationToken);
    }

    private HttpClient Client()
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("agent-brain", CurrentVersion.ToString()));
        return client;
    }
}
