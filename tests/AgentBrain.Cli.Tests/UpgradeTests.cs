using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentBrain.Application.Common;
using AgentBrain.Cli.Update;

namespace AgentBrain.Cli.Tests;

/// <summary>brain upgrade against a fake GitHub: release lookup, checksum verification, in-place replacement.</summary>
public sealed class UpgradeTests : IDisposable
{
    private readonly string _install = Directory.CreateTempSubdirectory("brain-install-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_install, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private sealed class FakeGitHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add($"{url} auth={(request.Headers.Authorization is null ? "no" : "yes")}");
            return Task.FromResult(files.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return stream.ToArray();
    }

    private static byte[] TarGz(params (string Name, string Text)[] files)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var (name, text) in files)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
            }
        }

        return stream.ToArray();
    }

    private static FakeGitHub Release(string tag, string asset, byte[] archive, string? checksumOverride = null)
    {
        const string Download = "https://github.com/me/brain/releases/download";
        var json = $$"""
            { "tag_name": "{{tag}}", "assets": [
              { "name": "{{asset}}", "browser_download_url": "{{Download}}/{{tag}}/{{asset}}" },
              { "name": "SHA256SUMS", "browser_download_url": "{{Download}}/{{tag}}/SHA256SUMS" } ] }
            """;
        var sum = checksumOverride ?? Convert.ToHexStringLower(SHA256.HashData(archive));
        return new FakeGitHub(new Dictionary<string, byte[]>
        {
            ["https://api.github.com/repos/me/brain/releases/latest"] = Encoding.UTF8.GetBytes(json),
            [$"https://api.github.com/repos/me/brain/releases/tags/{tag}"] = Encoding.UTF8.GetBytes(json),
            [$"{Download}/{tag}/{asset}"] = archive,
            [$"{Download}/{tag}/SHA256SUMS"] = Encoding.UTF8.GetBytes($"{sum}  {asset}\n{new string('0', 64)}  other.zip\n"),
        });
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4", true)]
    [InlineData("v1.2.3", "1.3.0", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("0.0.0-dev+abc123", "0.0.1", true)]
    [InlineData("1.0.0-dev", "1.0.0", true)]
    [InlineData("2.0.0", "1.9.9", false)]
    public void Versions_compare_as_semantic_versions(string current, string candidate, bool newer)
    {
        Assert.True(ReleaseVersion.TryParse(current, out var a));
        Assert.True(ReleaseVersion.TryParse(candidate, out var b));
        Assert.Equal(newer, b > a);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("latest")]
    [InlineData("1.2.x")]
    public void Non_versions_are_rejected(string text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public async Task Latest_release_is_found_and_the_token_goes_only_to_the_api()
    {
        var github = Release("v0.4.0", "brain-win-x64.zip", Zip(("brain.exe", "new")));
        var release = await new ReleaseUpdater("me/brain", github, token: "secret").FindAsync(null, TestContext.Current.CancellationToken);

        Assert.True(release.IsSuccess, release.Error?.Message);
        Assert.Equal("0.4.0", release.Value.Version.ToString());
        Assert.Contains("brain-win-x64.zip", release.Value.Assets.Keys);

        await new ReleaseUpdater("me/brain", github, token: "secret").InstallAsync(release.Value, "brain-win-x64.zip", _install, TestContext.Current.CancellationToken);
        Assert.All(github.Requests.Where(r => !r.Contains("api.github.com", StringComparison.Ordinal)), r => Assert.EndsWith("auth=no", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_zip_release_replaces_the_installed_files_and_keeps_the_old_ones_aside()
    {
        File.WriteAllText(Path.Combine(_install, "brain.exe"), "old");
        File.WriteAllText(Path.Combine(_install, "e_sqlite3.dll"), "old");
        File.WriteAllText(Path.Combine(_install, "unrelated.txt"), "keep");
        var github = Release("v0.4.0", "brain-win-x64.zip", Zip(("brain.exe", "new"), ("e_sqlite3.dll", "new")));
        var updater = new ReleaseUpdater("me/brain", github);

        var release = await updater.FindAsync(null, TestContext.Current.CancellationToken);
        var installed = await updater.InstallAsync(release.Value, "brain-win-x64.zip", _install, TestContext.Current.CancellationToken);

        Assert.True(installed.IsSuccess, installed.Error?.Message);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_install, "brain.exe")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_install, "e_sqlite3.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_install, "brain.exe.old")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_install, "unrelated.txt")));

        ReleaseUpdater.RemoveLeftovers(_install);
        Assert.Empty(Directory.EnumerateFiles(_install, "*.old"));
    }

    [Fact]
    public async Task A_tar_release_installs_too()
    {
        var github = Release("v1.0.0", "brain-linux-x64.tar.gz", TarGz(("brain", "new"), ("libe_sqlite3.so", "lib")));
        var updater = new ReleaseUpdater("me/brain", github);

        var release = await updater.FindAsync(ReleaseVersion.TryParse("1.0.0", out var v) ? v : null, TestContext.Current.CancellationToken);
        var installed = await updater.InstallAsync(release.Value, "brain-linux-x64.tar.gz", _install, TestContext.Current.CancellationToken);

        Assert.True(installed.IsSuccess, installed.Error?.Message);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_install, "brain")));
        Assert.Equal("lib", File.ReadAllText(Path.Combine(_install, "libe_sqlite3.so")));
    }

    [Fact]
    public async Task A_checksum_mismatch_installs_nothing()
    {
        File.WriteAllText(Path.Combine(_install, "brain.exe"), "old");
        var github = Release("v0.4.0", "brain-win-x64.zip", Zip(("brain.exe", "tampered")), checksumOverride: new string('a', 64));
        var updater = new ReleaseUpdater("me/brain", github);

        var release = await updater.FindAsync(null, TestContext.Current.CancellationToken);
        var installed = await updater.InstallAsync(release.Value, "brain-win-x64.zip", _install, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCode.ValidationFailed, installed.Error?.Code);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_install, "brain.exe")));
        Assert.Empty(Directory.EnumerateFiles(_install, "*.old"));
    }

    [Fact]
    public async Task Missing_releases_and_assets_are_reported()
    {
        var github = Release("v0.4.0", "brain-win-x64.zip", Zip(("brain.exe", "new")));
        var updater = new ReleaseUpdater("me/brain", github);

        Assert.Equal(ErrorCode.NotFound, (await updater.FindAsync(new ReleaseVersion(9, 9, 9, null), TestContext.Current.CancellationToken)).Error?.Code);

        var release = await updater.FindAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCode.NotFound, (await updater.InstallAsync(release.Value, "brain-osx-arm64.tar.gz", _install, TestContext.Current.CancellationToken)).Error?.Code);
    }

    [Fact]
    public void Checksum_lines_are_matched_by_exact_file_name()
    {
        var sums = $"{new string('a', 64)}  brain-win-x64.zip\n{new string('b', 64)} *brain-win-arm64.zip\n";
        Assert.Equal(new string('a', 64), ReleaseUpdater.ExpectedChecksum(sums, "brain-win-x64.zip"));
        Assert.Equal(new string('b', 64), ReleaseUpdater.ExpectedChecksum(sums, "brain-win-arm64.zip"));
        Assert.Null(ReleaseUpdater.ExpectedChecksum(sums, "brain-win"));
    }

    [Fact]
    public void This_platform_has_an_asset_name() =>
        Assert.Matches("^brain-(win|osx|linux)-(x64|arm64)\\.(zip|tar\\.gz)$", ReleaseUpdater.AssetName());
}
