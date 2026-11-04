using System.Text.RegularExpressions;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Decisions;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Application.Configuration;

/// <summary>
/// Merges configuration levels into the effective configuration (plan §7.4):
/// <list type="bullet">
/// <item>single values: the highest level that sets one wins;</item>
/// <item>collections and connections: merged by name, a higher entry replaces a lower one whole, <c>enabled: false</c> removes it;</item>
/// <item>aliases: union by key, the higher level wins a key;</item>
/// <item>project-only fields in the user file are ignored with a warning; <c>inheritUser: false</c> drops the user file;</item>
/// <item>paths resolve against the directory of the file that declares them.</item>
/// </list>
/// Every effective value records its origin for <c>brain config show --origin</c>.
/// </summary>
public static partial class ConfigLayering
{
    public static ConfigResolution Resolve(ConfigurationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new Resolver(input).Resolve();
    }

    private sealed class Resolver
    {
        private readonly ConfigurationInput _input;
        private readonly List<ConfigDiagnostic> _diagnostics = [];
        private readonly Dictionary<string, ConfigOrigin> _origins = new(StringComparer.Ordinal);
        private readonly List<LoadedConfigDocument> _layers;

        public Resolver(ConfigurationInput input)
        {
            _input = input;
            // Built-in defaults only hold project-relative paths; without a project they are never resolved.
            var builtInBase = input.Project?.ConfigDirectory ?? input.WorkingDirectory ?? "";
            _layers =
            [
                new LoadedConfigDocument(ConfigLevel.BuiltIn, null, builtInBase, ConfigDefaults.Document),
                .. input.Documents.OrderBy(d => d.Level),
            ];
        }

        /// <summary>Highest precedence first.</summary>
        private IEnumerable<LoadedConfigDocument> Descending => Enumerable.Reverse(_layers);

        public ConfigResolution Resolve()
        {
            ApplyUserFileRules();
            CheckSchemas();

            var collections = ResolveCollections();
            var connections = ResolveConnections();
            var project = ResolveProject();
            var search = ResolveSearch();
            var get = ResolveGet();
            var decisions = ResolveDecisions();
            var aliases = ResolveAliases();
            var memoryWrite = ResolveMemoryWrite(collections);
            var knowledgeWrite = ResolveKnowledgeWrite(collections, connections);
            var sourcePreference = ResolveSourcePreference(collections, connections);
            var runtimeDir = _input.Project is null ? null : PickPath("runtimeDir", d => d.RuntimeDir);
            var proposalsDir = _input.Project is null ? null : PickPath("proposals", d => d.Proposals);

            var configuration = new EffectiveConfiguration(
                project, collections, connections, memoryWrite, knowledgeWrite, sourcePreference, search, get, aliases,
                runtimeDir, proposalsDir, _origins, decisions);
            return new ConfigResolution(configuration, _diagnostics);
        }

        private void ApplyUserFileRules()
        {
            var inherit = Pick("inheritUser", d => d.InheritUser, levels: [ConfigLevel.Project, ConfigLevel.ProjectLocal, ConfigLevel.Invocation]);
            if (inherit == false)
            {
                _layers.RemoveAll(l => l.Level == ConfigLevel.User);
                return;
            }

            for (var i = 0; i < _layers.Count; i++)
            {
                var layer = _layers[i];
                if (layer.Level != ConfigLevel.User)
                {
                    continue;
                }

                var d = layer.Document;
                var origin = Origin(layer);
                foreach (var (field, present) in new[]
                {
                    ("project", d.Project is not null),
                    ("writes.memory", d.Writes?.Memory is not null),
                    ("runtimeDir", d.RuntimeDir is not null),
                    ("proposals", d.Proposals is not null),
                    ("inheritUser", d.InheritUser is not null),
                })
                {
                    if (present)
                    {
                        Warn(field, $"'{field}' is project-only and is ignored in the user file.", origin);
                    }
                }

                _layers[i] = layer with { Document = d with { Project = null, Writes = d.Writes is null ? null : d.Writes with { Memory = null }, RuntimeDir = null, Proposals = null, InheritUser = null } };
            }
        }

        private void CheckSchemas()
        {
            foreach (var layer in _layers.Where(l => l.Document.Schema is { } s && s != ConfigDefaults.Schema))
            {
                Fail("schema", $"Configuration schema {layer.Document.Schema} is not supported; this version reads schema {ConfigDefaults.Schema}.", Origin(layer));
            }
        }

        private ProjectSettings? ResolveProject()
        {
            if (_input.Project is null)
            {
                return null;
            }

            var id = Pick("project.id", d => d.Project?.Id);
            if (id is null)
            {
                Fail("project.id", "The project configuration must set 'project.id'.", null);
                return null;
            }

            if (!Names.IsProjectId(id))
            {
                Fail("project.id", $"'{id}' is not a valid project ID (lowercase segments joined by '.' or '-').", _origins["project.id"]);
                return null;
            }

            var technologies = NameList("project.technologies", Pick("project.technologies", d => d.Project?.Technologies), Names.IsTechnicalName);
            var packages = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, version) in Pick("project.packages", d => d.Project?.Packages) ?? new Dictionary<string, string>())
            {
                var normalized = name.Trim().ToLowerInvariant();
                if (!Names.IsTechnicalName(normalized) || string.IsNullOrWhiteSpace(version))
                {
                    Fail($"project.packages.{name}", "Package entries need a valid name and a version.", _origins["project.packages"]);
                    continue;
                }

                packages[normalized] = version.Trim();
            }

            return new ProjectSettings(id, _input.Project.RootDirectory, technologies, packages);
        }

        private List<RegisteredCollection> ResolveCollections()
        {
            var merged = MergeByName("collections", d => d.Collections, r => r.Enabled == false);
            var collections = new List<RegisteredCollection>(merged.Count);
            foreach (var (name, (registration, layer)) in merged)
            {
                var path = $"collections.{name}";
                var origin = Origin(layer);
                if (!CollectionId.TryParse(name, out var id))
                {
                    Fail(path, $"'{name}' is not a valid collection ID.", origin);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(registration.Path))
                {
                    Fail($"{path}.path", "A collection registration needs a 'path'.", origin);
                    continue;
                }

                var capabilities = Capability.None;
                foreach (var capabilityName in registration.Capabilities ?? ConfigDefaults.Capabilities)
                {
                    if (Capabilities.Names.TryParse(capabilityName, out var capability))
                    {
                        capabilities |= capability;
                    }
                    else
                    {
                        Fail($"{path}.capabilities", $"'{capabilityName}' is not one of: {Capabilities.Names.Joined}.", origin);
                    }
                }

                if (capabilities != Capability.None && !capabilities.HasFlag(Capability.Read))
                {
                    Warn($"{path}.capabilities", "Capabilities without 'read' make the collection invisible to search; 'read' was added.", origin);
                    capabilities |= Capability.Read;
                }

                collections.Add(new RegisteredCollection(id, ResolvePath(layer, registration.Path), capabilities, registration.ImmediatePublish ?? false, registration.AllowExternalDecisions ?? false));
            }

            return [.. collections.OrderBy(c => c.Id.Value, StringComparer.Ordinal)];
        }

        private List<EffectiveConnection> ResolveConnections()
        {
            var merged = MergeByName("connections", d => d.Connections, c => c.Enabled == false);
            var forwardContext = Pick("forwardContext", d => d.ForwardContext) ?? true;
            var connections = new List<EffectiveConnection>(merged.Count);
            foreach (var (name, (definition, layer)) in merged)
            {
                var path = $"connections.{name}";
                var origin = Origin(layer);
                var valid = true;
                if (!CollectionId.IsValid(name))
                {
                    Fail(path, $"'{name}' is not a valid connection name (lowercase segments joined by '.' or '-').", origin);
                    valid = false;
                }

                if (!Uri.TryCreate(definition.Url, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
                {
                    Fail($"{path}.url", "A connection needs an absolute https URL.", origin);
                    valid = false;
                }
                else if (url.Scheme == "http" && !url.IsLoopback)
                {
                    Fail($"{path}.url", "Plain http is only allowed for loopback addresses; use https.", origin);
                    valid = false;
                }

                if (definition.CredentialEnv is null || !EnvironmentVariableName().IsMatch(definition.CredentialEnv))
                {
                    Fail($"{path}.credentialEnv", "A connection needs 'credentialEnv': the name of the environment variable holding its key.", origin);
                    valid = false;
                }

                var hosted = new List<CollectionId>();
                foreach (var collection in definition.Collections ?? [])
                {
                    if (CollectionId.TryParse(collection, out var id))
                    {
                        hosted.Add(id);
                    }
                    else
                    {
                        Fail($"{path}.collections", $"'{collection}' is not a valid collection ID.", origin);
                        valid = false;
                    }
                }

                var timeout = definition.TimeoutMs ?? ConfigDefaults.ConnectionTimeoutMs;
                valid &= InRange($"{path}.timeoutMs", timeout, ConfigDefaults.TimeoutRange, origin);
                if (!valid)
                {
                    continue;
                }

                var forward = definition.Forward;
                var policy = forwardContext
                    ? new ForwardPolicy(forward?.Project ?? false, forward?.Technologies ?? false, forward?.Files ?? false, forward?.Symbols ?? false)
                    : ForwardPolicy.Nothing;
                connections.Add(new EffectiveConnection(name, url!, new CredentialReference(definition.CredentialEnv!), hosted, TimeSpan.FromMilliseconds(timeout), policy));
            }

            return [.. connections.OrderBy(c => c.Name, StringComparer.Ordinal)];
        }

        private SearchSettings ResolveSearch()
        {
            var relaxationText = Pick("search.relaxation", d => d.Search?.Relaxation)!;
            if (!Relaxation.TryParse(relaxationText, out var relaxation))
            {
                Fail("search.relaxation", $"'{relaxationText}' is not strict, default, broad or a number in [0, 1].", _origins["search.relaxation"]);
            }

            var kinds = new List<EntryKind>();
            foreach (var name in Pick("search.kinds", d => d.Search?.Kinds)!)
            {
                if (WireNames.Kinds.TryParse(name, out var kind))
                {
                    kinds.Add(kind);
                }
                else
                {
                    Fail("search.kinds", $"'{name}' is not one of: {WireNames.Kinds.Joined}.", _origins["search.kinds"]);
                }
            }

            var maxResults = PickInt("search.maxResults", d => d.Search?.MaxResults, ConfigDefaults.MaxResultsRange);
            var candidates = PickInt("search.candidatesPerSource", d => d.Search?.CandidatesPerSource, ConfigDefaults.CandidatesRange);
            if (candidates < maxResults)
            {
                Warn("search.candidatesPerSource", "Fewer candidates per source than results requested limits recall.", _origins["search.candidatesPerSource"]);
            }

            return new SearchSettings(
                relaxation,
                maxResults,
                PickInt("search.maxTokens", d => d.Search?.MaxTokens, ConfigDefaults.MaxTokensRange),
                PickInt("search.maxBytes", d => d.Search?.MaxBytes, ConfigDefaults.MaxBytesRange),
                candidates,
                kinds,
                Pick("search.log", d => d.Search?.Log) ?? false);
        }

        private DecisionSettings ResolveDecisions()
        {
            var defaults = JevSettings.Default;
            var env = Pick("decisions.jev.apiKeyEnv", d => d.Decisions?.Jev?.ApiKeyEnv) ?? DecisionDefaults.ApiKeyEnv;
            if (!EnvironmentVariableName().IsMatch(env))
            {
                Fail("decisions.jev.apiKeyEnv", "'apiKeyEnv' is the name of the environment variable holding the key, not the key itself.", _origins.GetValueOrDefault("decisions.jev.apiKeyEnv"));
                env = DecisionDefaults.ApiKeyEnv;
            }

            var model = Pick("decisions.jev.model", d => d.Decisions?.Jev?.Model) ?? defaults.Model;
            if (string.IsNullOrWhiteSpace(model))
            {
                Fail("decisions.jev.model", "A model ID is required.", _origins.GetValueOrDefault("decisions.jev.model"));
                model = DecisionDefaults.Model;
            }

            var timeout = PickOptionalInt("decisions.jev.timeoutMs", d => d.Decisions?.Jev?.TimeoutMs, DecisionDefaults.TimeoutRange) ?? DecisionDefaults.TimeoutMs;
            var concurrency = PickOptionalInt("decisions.jev.maxConcurrency", d => d.Decisions?.Jev?.MaxConcurrency, DecisionDefaults.ConcurrencyRange) ?? DecisionDefaults.MaxConcurrency;
            var calls = PickOptionalInt("decisions.jev.maxCallsPerOperation", d => d.Decisions?.Jev?.MaxCallsPerOperation, DecisionDefaults.CallsRange) ?? DecisionDefaults.MaxCallsPerOperation;
            var pool = PickOptionalInt("decisions.jev.relevancePool", d => d.Decisions?.Jev?.RelevancePool, DecisionDefaults.PoolRange) ?? DecisionDefaults.RelevancePool;
            var confidence = Pick("decisions.jev.minConfidence", d => d.Decisions?.Jev?.MinConfidence) ?? DecisionDefaults.MinConfidence;
            if (confidence is < 0 or > 1)
            {
                Fail("decisions.jev.minConfidence", $"{confidence} is outside the accepted range 0–1.", _origins["decisions.jev.minConfidence"]);
                confidence = DecisionDefaults.MinConfidence;
            }

            var jev = new JevSettings(new CredentialReference(env), model.Trim(), TimeSpan.FromMilliseconds(timeout), concurrency, calls, confidence, pool);
            var relevance = PickMode("decisions.relevance", d => d.Decisions?.Relevance, allowHybrid: true, "heuristic, jev or hybrid");
            var classifier = PickMode("decisions.classifier", d => d.Decisions?.Classifier, allowHybrid: false, "heuristic or jev");
            var duplicates = PickMode("decisions.duplicates", d => d.Decisions?.Duplicates, allowHybrid: false, "heuristic or jev");
            var gateText = Pick("decisions.captureGate", d => d.Decisions?.CaptureGate)?.Trim().ToLowerInvariant();
            var gate = gateText switch
            {
                null or "off" => false,
                "jev" => true,
                _ => FailBool("decisions.captureGate", $"'{gateText}' is not off or jev."),
            };

            var settings = new DecisionSettings(relevance, classifier, duplicates, gate, jev);
            if (settings.UsesModel && jev.IsAlias)
            {
                Warn("decisions.jev.model", $"'{jev.Model}' is an alias that moves to newer models; pin a version such as '{DecisionDefaults.Model}' so evaluation results stay comparable.", _origins.GetValueOrDefault("decisions.jev.model"));
            }

            return settings;
        }

        private DecisionMode PickMode(string path, Func<ConfigDocument, string?> select, bool allowHybrid, string expected)
        {
            var text = Pick(path, select)?.Trim().ToLowerInvariant();
            if (text is null)
            {
                return DecisionMode.Heuristic;
            }

            if (DecisionModes.Names.TryParse(text, out var mode) && (allowHybrid || mode != DecisionMode.Hybrid))
            {
                return mode;
            }

            Fail(path, $"'{text}' is not {expected}.", _origins[path]);
            return DecisionMode.Heuristic;
        }

        private bool FailBool(string path, string message)
        {
            Fail(path, message, _origins.GetValueOrDefault(path));
            return false;
        }

        private int? PickOptionalInt(string path, Func<ConfigDocument, int?> select, (int Min, int Max) range)
        {
            if (Pick(path, select) is not { } value)
            {
                return null;
            }

            return InRange(path, value, range, _origins[path]) ? value : Math.Clamp(value, range.Min, range.Max);
        }

        private GetSettings ResolveGet() => new(
            PickInt("get.maxBytes", d => d.Get?.MaxBytes, ConfigDefaults.GetBytesRange),
            PickInt("get.batchTargets", d => d.Get?.BatchTargets, ConfigDefaults.BatchTargetsRange),
            PickInt("get.batchBytes", d => d.Get?.BatchBytes, ConfigDefaults.GetBytesRange));

        private List<AliasGroup> ResolveAliases()
        {
            var byKey = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var layer in _layers)
            {
                foreach (var (key, values) in layer.Document.Aliases ?? new Dictionary<string, IReadOnlyList<string>>())
                {
                    var term = key.Trim().ToLowerInvariant();
                    var expansions = values.Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0 && v != term).Distinct().ToList();
                    if (term.Length == 0 || expansions.Count == 0)
                    {
                        Fail($"aliases.{key}", "An alias needs a term and at least one different equivalent.", Origin(layer));
                        continue;
                    }

                    byKey[term] = expansions;
                    _origins[$"aliases.{term}"] = Origin(layer);
                }
            }

            return [.. byKey.Select(kv => new AliasGroup([kv.Key, .. kv.Value]))];
        }

        private CollectionId? ResolveMemoryWrite(List<RegisteredCollection> collections)
        {
            var name = Pick("writes.memory", d => d.Writes?.Memory);
            if (name is null)
            {
                return null;
            }

            var origin = _origins["writes.memory"];
            var collection = collections.Find(c => c.Id.Value == name);
            if (collection is null)
            {
                Fail("writes.memory", $"'{name}' is not a configured collection.", origin);
                return null;
            }

            if (!collection.Capabilities.HasFlag(Capability.Maintain))
            {
                Fail("writes.memory", $"'{name}' does not have the 'maintain' capability, so it cannot be the project memory destination.", origin);
                return null;
            }

            return collection.Id;
        }

        /// <summary>
        /// Where reusable knowledge goes. Allowed in the user file. A local target needs maintain or propose (a
        /// propose-only target turns saves into proposals); a remote one must belong to a connection, and its
        /// capabilities are checked against the host when writing.
        /// </summary>
        private CollectionId? ResolveKnowledgeWrite(List<RegisteredCollection> collections, List<EffectiveConnection> connections)
        {
            var name = Pick("writes.knowledge", d => d.Writes?.Knowledge);
            if (name is null)
            {
                return null;
            }

            var origin = _origins["writes.knowledge"];
            if (!CollectionId.TryParse(name, out var id))
            {
                Fail("writes.knowledge", $"'{name}' is not a valid collection ID.", origin);
                return null;
            }

            if (collections.Find(c => c.Id == id) is { } local)
            {
                if (!local.Capabilities.HasFlag(Capability.Maintain) && !local.Capabilities.HasFlag(Capability.Propose))
                {
                    Fail("writes.knowledge", $"'{name}' has neither 'maintain' nor 'propose', so nothing can be saved to it.", origin);
                    return null;
                }

                return id;
            }

            // A connection without a collection list serves whatever its key can read; the host decides.
            if (connections.Any(c => c.Collections.Count == 0 || c.Collections.Contains(id)))
            {
                return id;
            }

            Fail("writes.knowledge", $"'{name}' is neither a configured collection nor served by a connection.", origin);
            return null;
        }

        private List<string> ResolveSourcePreference(List<RegisteredCollection> collections, List<EffectiveConnection> connections)
        {
            var preference = Pick("sourcePreference", d => d.SourcePreference) ?? [];
            var known = collections.Select(c => c.Id.Value).Concat(connections.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var name in preference.Where(n => !known.Contains(n)))
            {
                Warn("sourcePreference", $"'{name}' is not a configured collection or connection.", _origins["sourcePreference"]);
            }

            return [.. preference];
        }

        private SortedDictionary<string, (T Value, LoadedConfigDocument Layer)> MergeByName<T>(
            string section, Func<ConfigDocument, IReadOnlyDictionary<string, T>?> select, Func<T, bool> isDisabled)
        {
            var merged = new SortedDictionary<string, (T, LoadedConfigDocument)>(StringComparer.Ordinal);
            foreach (var layer in _layers)
            {
                foreach (var (name, value) in select(layer.Document) ?? new Dictionary<string, T>())
                {
                    _origins[$"{section}.{name}"] = Origin(layer);
                    if (isDisabled(value))
                    {
                        merged.Remove(name);
                    }
                    else
                    {
                        merged[name] = (value, layer);
                    }
                }
            }

            return merged;
        }

        private T? Pick<T>(string path, Func<ConfigDocument, T?> select, ConfigLevel[]? levels = null)
            where T : class
        {
            foreach (var layer in Descending.Where(l => levels is null || levels.Contains(l.Level)))
            {
                if (select(layer.Document) is { } value)
                {
                    _origins[path] = Origin(layer);
                    return value;
                }
            }

            return null;
        }

        private T? Pick<T>(string path, Func<ConfigDocument, T?> select, ConfigLevel[]? levels = null)
            where T : struct
        {
            foreach (var layer in Descending.Where(l => levels is null || levels.Contains(l.Level)))
            {
                if (select(layer.Document) is { } value)
                {
                    _origins[path] = Origin(layer);
                    return value;
                }
            }

            return null;
        }

        private int PickInt(string path, Func<ConfigDocument, int?> select, (int Min, int Max) range)
        {
            var value = Pick(path, select)!.Value;
            return InRange(path, value, range, _origins[path]) ? value : Math.Clamp(value, range.Min, range.Max);
        }

        private string? PickPath(string path, Func<ConfigDocument, string?> select)
        {
            foreach (var layer in Descending)
            {
                if (select(layer.Document) is { Length: > 0 } value)
                {
                    _origins[path] = Origin(layer);
                    return ResolvePath(layer, value);
                }
            }

            return null;
        }

        private List<string> NameList(string path, IReadOnlyList<string>? values, Func<string, bool> isValid)
        {
            var names = new List<string>();
            foreach (var value in values ?? [])
            {
                var name = value.Trim().ToLowerInvariant();
                if (isValid(name))
                {
                    names.Add(name);
                }
                else
                {
                    Fail(path, $"'{value}' is not a valid name.", _origins.GetValueOrDefault(path));
                }
            }

            return names;
        }

        private bool InRange(string path, int value, (int Min, int Max) range, ConfigOrigin? origin)
        {
            if (value >= range.Min && value <= range.Max)
            {
                return true;
            }

            Fail(path, $"{value} is outside the accepted range {range.Min}–{range.Max}.", origin);
            return false;
        }

        private static string ResolvePath(LoadedConfigDocument layer, string path) =>
            Path.GetFullPath(Path.Combine(layer.BaseDirectory, path));

        private static ConfigOrigin Origin(LoadedConfigDocument layer) => new(layer.Level, layer.FilePath);

        private void Fail(string path, string message, ConfigOrigin? origin) =>
            _diagnostics.Add(new ConfigDiagnostic(IssueSeverity.Error, path, message, origin));

        private void Warn(string path, string message, ConfigOrigin? origin) =>
            _diagnostics.Add(new ConfigDiagnostic(IssueSeverity.Warning, path, message, origin));
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableName();
}
