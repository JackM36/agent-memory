using System.CommandLine;
using System.Globalization;
using AgentBrain.Application.Configuration;
using AgentBrain.Composition;
using AgentBrain.Infrastructure.Local.Configuration;
using AgentBrain.Server;

// brain-server: hosts Agent Brain collections over HTTPS (docs/deployment.md).
var data = new Option<string>("--data")
{
    Description = "Data directory (host.json, database, index, proposals, audit, backups).",
    Recursive = true,
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("AGENT_BRAIN_DATA") ?? "/data",
};

var root = new RootCommand("brain-server: host Agent Brain collections for remote clients.") { data };

var collection = new Option<string>("--collection") { Description = "ID of the first collection.", DefaultValueFactory = _ => "personal" };
var title = new Option<string>("--title") { Description = "Its title.", DefaultValueFactory = _ => "Personal knowledge" };
var init = new Command("init", "Create host.json, the database and two API keys (admin and read/write). Prints the keys once.") { collection, title };
init.SetAction(async (parse, ct) =>
{
    var directory = Path.GetFullPath(parse.GetValue(data)!);
    Directory.CreateDirectory(directory);
    var hostFile = Path.Combine(directory, HostConfigFile.FileName);
    if (!File.Exists(hostFile))
    {
        await File.WriteAllTextAsync(hostFile, HostConfigFile.Initial(parse.GetValue(collection)!, parse.GetValue(title)!), ct);
        Console.WriteLine($"created {hostFile}");
    }

    using var runtime = Open(directory);
    if ((await runtime.Keys.CreateAsync("admin", ServerApp.AdminScopes, ct)) is not { IsSuccess: true } admin
        || (await runtime.Keys.CreateAsync("read-write", ServerApp.ReadWriteScopes, ct)) is not { IsSuccess: true } readWrite)
    {
        Console.Error.WriteLine("Could not create keys.");
        return 1;
    }

    Console.WriteLine("Store these keys now; they are not shown again.");
    Console.WriteLine($"admin key      ({admin.Value.Record.KeyId}): {admin.Value.Key}");
    Console.WriteLine($"read/write key ({readWrite.Value.Record.KeyId}): {readWrite.Value.Key}");
    return 0;
});

var run = new Command("run", "Serve the API.");
run.SetAction(async (parse, ct) =>
{
    using var runtime = Open(Path.GetFullPath(parse.GetValue(data)!));
    var app = ServerApp.Build(runtime);
    await app.RunAsync(ct);
    return 0;
});

var scopes = new Option<string[]>("--scope") { Description = "collection:<id|*>:read|propose|maintain|publish, or admin. Repeatable.", Required = true, AllowMultipleArgumentsPerToken = false };
var label = new Option<string>("--label") { Description = "Who or what uses the key.", Required = true };
var create = new Command("create", "Create a key. It is printed once.") { scopes, label };
create.SetAction(async (parse, ct) =>
{
    using var runtime = Open(Path.GetFullPath(parse.GetValue(data)!));
    var key = await runtime.Keys.CreateAsync(parse.GetValue(label)!, parse.GetValue(scopes)!, ct);
    if (!key.IsSuccess)
    {
        Console.Error.WriteLine(key.Error.Message);
        return 2;
    }

    Console.WriteLine($"{key.Value.Record.KeyId}: {key.Value.Key}");
    return 0;
});

var list = new Command("list", "List keys (never their secrets).");
list.SetAction(async (parse, ct) =>
{
    using var runtime = Open(Path.GetFullPath(parse.GetValue(data)!));
    var store = new AgentBrain.Infrastructure.Sqlite.SqliteApiKeyStore(runtime.Database);
    foreach (var key in await store.ListAsync(ct))
    {
        var state = key.Revoked is { } at ? $"revoked {at:yyyy-MM-dd}" : "active";
        Console.WriteLine($"{key.KeyId}  {key.Label,-20} {state,-18} created {key.Created:yyyy-MM-dd}  {string.Join(' ', key.Scopes)}");
    }

    return 0;
});

var keyId = new Argument<string>("key-id");
var revoke = new Command("revoke", "Revoke a key immediately.") { keyId };
revoke.SetAction(async (parse, ct) =>
{
    using var runtime = Open(Path.GetFullPath(parse.GetValue(data)!));
    var store = new AgentBrain.Infrastructure.Sqlite.SqliteApiKeyStore(runtime.Database);
    var revoked = await store.RevokeAsync(parse.GetValue(keyId)!, DateTimeOffset.UtcNow, ct);
    Console.WriteLine(revoked ? "revoked" : "no active key with that ID");
    return revoked ? 0 : 3;
});

var destination = new Argument<string>("path") { Description = "Backup file to write.", Arity = ArgumentArity.ZeroOrOne };
var backup = new Command("backup", "Copy the database with SQLite's online backup (safe while running).") { destination };
backup.SetAction(parse =>
{
    var directory = Path.GetFullPath(parse.GetValue(data)!);
    using var runtime = Open(directory);
    var target = parse.GetValue(destination)
        ?? Path.Combine(HostRuntime.Paths.Backups(directory), $"brain-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db");
    runtime.Database.Backup(target);
    Console.WriteLine($"backed up to {target}");
    return 0;
});

var health = new Command("health", "Exit 0 when the local server answers /health (for container health checks).");
health.SetAction(async (parse, ct) =>
{
    var configuration = HostRuntime.ReadConfiguration(Path.GetFullPath(parse.GetValue(data)!));
    if (!configuration.IsSuccess)
    {
        return 1;
    }

    using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        using var response = await client.GetAsync(ServerApp.HealthUrl(configuration.Value), ct);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
});

var newId = new Argument<string>("id") { Description = "Collection ID, e.g. library or library.unity." };
var newTitle = new Option<string?>("--title") { Description = "Title (default: from the ID)." };
var storage = new Option<string>("--storage") { Description = "sqlite (default) or markdown.", DefaultValueFactory = _ => "sqlite" };
var folder = new Option<string?>("--path") { Description = "Markdown: folder under the data directory (default: collections/<id>). Created with a collection.json if missing." };
var technologies = new Option<string[]>("--tech") { Description = "Technology every entry applies to by default. Repeatable.", AllowMultipleArgumentsPerToken = false };
var readOnly = new Option<bool>("--read-only") { Description = "Nobody can write it through the API." };
var immediate = new Option<bool>("--immediate-publish") { Description = "Allow publishing without review for keys with publish." };
var addCollection = new Command("add", "Host another collection. Restart the server to serve it.") { newId, newTitle, storage, folder, technologies, readOnly, immediate };
addCollection.SetAction(async (parse, ct) =>
{
    var directory = Path.GetFullPath(parse.GetValue(data)!);
    var hostFile = Path.Combine(directory, HostConfigFile.FileName);
    if (!AgentBrain.Domain.Identity.CollectionId.TryParse(parse.GetValue(newId), out var id))
    {
        Console.Error.WriteLine($"'{parse.GetValue(newId)}' is not a valid collection ID (lowercase words joined by '-' or '.').");
        return 2;
    }

    if (!File.Exists(hostFile))
    {
        Console.Error.WriteLine($"{hostFile} does not exist; run brain-server init first.");
        return 2;
    }

    var markdown = parse.GetValue(storage) switch
    {
        "sqlite" => false,
        "markdown" => true,
        _ => (bool?)null,
    };
    if (markdown is null)
    {
        Console.Error.WriteLine("--storage must be sqlite or markdown.");
        return 2;
    }

    var title = parse.GetValue(newTitle) ?? CollectionDescriptorFile.TitleFrom(id.Value);
    var techs = parse.GetValue(technologies) ?? [];
    var relative = markdown.Value ? (parse.GetValue(folder) ?? Path.Combine("collections", id.Value)).Replace('\\', '/') : null;
    var hosted = new HostedCollection(
        id, markdown.Value ? HostStorage.Markdown : HostStorage.Sqlite, relative, title,
        new AgentBrain.Domain.Entries.ApplicabilityOverrides(AgentBrain.Domain.Entries.ApplicabilityScope.General, null, techs),
        AgentBrain.Domain.Entries.ProvenanceSource.Curated, parse.GetValue(readOnly), parse.GetValue(immediate));
    var updated = HostConfigFile.AddCollection(await File.ReadAllTextAsync(hostFile, ct), hostFile, hosted);
    if (!updated.IsSuccess)
    {
        Console.Error.WriteLine(updated.Error.Message);
        return 2;
    }

    if (relative is not null)
    {
        var root = Path.GetFullPath(relative, directory);
        var descriptor = Path.Combine(root, CollectionDescriptorFile.FileName);
        if (!File.Exists(descriptor))
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(descriptor, CollectionDescriptorFile.Create(id.Value, title, techs, parse.GetValue(readOnly)), ct);
            Console.WriteLine($"created {descriptor}");
        }
        else if (CollectionDescriptorFile.ReadId(descriptor) != id.Value)
        {
            Console.Error.WriteLine($"{descriptor} describes a different collection.");
            return 2;
        }
    }

    await File.WriteAllTextAsync(hostFile, updated.Value, ct);
    Console.WriteLine($"updated {hostFile}: collection '{id}' ({(markdown.Value ? "markdown " + relative : "sqlite")}). Restart the server to serve it.");
    Console.WriteLine($"Keys with collection:*:<capability> scopes reach it already; others need e.g. keys create --scope collection:{id}:read.");
    return 0;
});

var listCollections = new Command("list", "List hosted collections.");
listCollections.SetAction(parse =>
{
    var configuration = HostRuntime.ReadConfiguration(Path.GetFullPath(parse.GetValue(data)!));
    if (!configuration.IsSuccess)
    {
        Console.Error.WriteLine(configuration.Error.Message);
        return 2;
    }

    foreach (var c in configuration.Value.Collections)
    {
        Console.WriteLine($"{c.Id}  {c.Storage.ToString().ToLowerInvariant()}{(c.Path is null ? "" : " " + c.Path)}{(c.ReadOnly ? "  read-only" : "")}{(c.ImmediatePublish ? "  immediate-publish" : "")}  {c.Title}");
    }

    return 0;
});

root.Subcommands.Add(init);
root.Subcommands.Add(new Command("collections", "Manage hosted collections.") { addCollection, listCollections });
root.Subcommands.Add(run);
root.Subcommands.Add(new Command("keys", "Manage API keys.") { create, list, revoke });
root.Subcommands.Add(backup);
root.Subcommands.Add(health);
return await root.Parse(args).InvokeAsync();

static HostRuntime Open(string directory)
{
    var configuration = HostRuntime.ReadConfiguration(directory);
    if (!configuration.IsSuccess)
    {
        throw new InvalidOperationException(configuration.Error.Message);
    }

    var runtime = HostRuntime.Open(directory, configuration.Value);
    return runtime.IsSuccess ? runtime.Value : throw new InvalidOperationException(runtime.Error.Message);
}
