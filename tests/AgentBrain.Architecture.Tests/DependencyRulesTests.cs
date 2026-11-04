using System.Xml.Linq;

namespace AgentBrain.Architecture.Tests;

/// <summary>
/// Enforces the dependency rules in _SPECS/implementation-plan.md §2.2 by reading the declared
/// project and package references of every project under src/.
/// </summary>
public sealed class DependencyRulesTests
{
    private const string P = "AgentBrain.";

    private static readonly Dictionary<string, Rule> Rules = new()
    {
        [P + "Domain"] = new([], []),
        [P + "Application"] = new([P + "Domain"], ["Microsoft.Extensions.Logging.Abstractions"]),
        [P + "Contracts"] = new([P + "Application"], []),
        [P + "Infrastructure.Markdown"] = new([P + "Application"], ["Markdig", "YamlDotNet"]),
        [P + "Infrastructure.Sqlite"] = new([P + "Application"], ["Microsoft.Data.Sqlite"]),
        [P + "Infrastructure.Local"] = new([P + "Application"], []),
        [P + "Infrastructure.Http"] = new([P + "Application", P + "Contracts"], []),
        [P + "Infrastructure.Jev"] = new([P + "Application"], []),
        [P + "Composition"] = new(
            [P + "Application", P + "Contracts", P + "Infrastructure.Local", P + "Infrastructure.Markdown", P + "Infrastructure.Sqlite", P + "Infrastructure.Http", P + "Infrastructure.Jev"],
            []),
        [P + "Mcp"] = new([P + "Composition", P + "Contracts", P + "Application"], ["ModelContextProtocol", "Microsoft.Extensions.Hosting"]),
        [P + "Integrations"] = new([P + "Composition", P + "Contracts", P + "Application"], ["Tomlyn"]),
        [P + "Cli"] = new(
            [P + "Composition", P + "Contracts", P + "Mcp", P + "Integrations", P + "Application", P + "Infrastructure.Local"],
            ["System.CommandLine"]),
        [P + "Server"] = new([P + "Composition", P + "Contracts", P + "Application"], ["System.CommandLine"]),
    };

    public static TheoryData<string> SourceProjects() => [.. LoadProjects().Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_has_a_dependency_rule(string project) =>
        Assert.True(Rules.ContainsKey(project), $"{project} has no rule in {nameof(DependencyRulesTests)}; add one before adding the project.");

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_references_only_allowed_projects(string project)
    {
        var declared = LoadProjects().Single(p => p.Name == project);
        var forbidden = declared.ProjectReferences.Except(Rules[project].Projects).ToList();
        Assert.True(forbidden.Count == 0, $"{project} must not reference: {string.Join(", ", forbidden)}");
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_references_only_allowed_packages(string project)
    {
        var declared = LoadProjects().Single(p => p.Name == project);
        var forbidden = declared.PackageReferences.Except(Rules[project].Packages).ToList();
        Assert.True(forbidden.Count == 0, $"{project} must not reference packages: {string.Join(", ", forbidden)}");
    }

    [Fact]
    public void Nothing_references_host_executables()
    {
        var offenders = LoadProjects()
            .Where(p => p.ProjectReferences.Any(r => r is P + "Cli" or P + "Server"))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(offenders);
    }

    private static IReadOnlyList<DeclaredProject> LoadProjects()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        return [.. Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories).Select(Parse).OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    private static DeclaredProject Parse(string path)
    {
        var xml = XDocument.Load(path);
        var projects = xml.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')))
            .ToHashSet(StringComparer.Ordinal);
        var packages = xml.Descendants("PackageReference")
            .Select(e => (string)e.Attribute("Include")!)
            .ToHashSet(StringComparer.Ordinal);
        return new DeclaredProject(Path.GetFileNameWithoutExtension(path), projects, packages);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentBrain.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (AgentBrain.slnx) not found.");
    }

    private sealed record Rule(IReadOnlyCollection<string> Projects, IReadOnlyCollection<string> Packages);

    private sealed record DeclaredProject(string Name, IReadOnlySet<string> ProjectReferences, IReadOnlySet<string> PackageReferences);
}
