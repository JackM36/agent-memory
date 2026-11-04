using System.Globalization;
using System.Text;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Matching;
using AgentBrain.Domain.Metadata;
using AgentBrain.Domain.References;
using AgentBrain.Domain.Revisions;
using AgentBrain.Domain.Validation;
using AgentBrain.Domain.Versions;
using Microsoft.Data.Sqlite;

namespace AgentBrain.Infrastructure.Sqlite;

/// <summary>
/// SQLite FTS5 search index over the local collections (plan §7.5). One connection per instance, WAL
/// journaling and a busy timeout so parallel agent sessions can read while one refreshes (plan §5.14).
/// Every query is parameterised; query terms are quoted, so FTS syntax in user text has no effect.
/// </summary>
public sealed class SqliteSearchIndex : ISearchIndex, IDisposable
{
    private const string DuplicateCode = "duplicate_id";

    private readonly SqliteConnection _connection;

    private SqliteSearchIndex(SqliteConnection connection, IndexTokenizer tokenizer)
    {
        _connection = connection;
        Tokenizer = tokenizer;
    }

    public IndexTokenizer Tokenizer { get; }

    /// <summary>Opens (creating or rebuilding as needed) the index database. Use <c>:memory:</c> for tests.</summary>
    public static SqliteSearchIndex Open(string path, IndexTokenizer tokenizer = IndexTokenizer.Porter)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path != ":memory:")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        var index = new SqliteSearchIndex(connection, tokenizer);
        index.Execute("PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;");
        if (path != ":memory:")
        {
            index.Execute("PRAGMA journal_mode = WAL;");
        }

        index.EnsureSchema();
        return index;
    }

    public void Dispose() => _connection.Dispose();

    public ValueTask<IReadOnlyDictionary<string, IndexedStamp>> GetStampsAsync(CollectionId collection, CancellationToken cancellationToken)
    {
        using var command = Command("SELECT path, size, mtime, hash FROM files WHERE collection = $c", ("$c", collection.Value));
        using var reader = command.ExecuteReader();
        var stamps = new Dictionary<string, IndexedStamp>(StringComparer.Ordinal);
        while (reader.Read())
        {
            stamps[reader.GetString(0)] = new IndexedStamp(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, IndexedStamp>>(stamps);
    }

    public ValueTask ApplyAsync(IndexChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var collection = changes.Collection.Value;
        using var transaction = _connection.BeginTransaction();
        foreach (var path in changes.RemovedPaths)
        {
            RemoveFile(collection, path);
        }

        foreach (var file in changes.Upserts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveFile(collection, file.Stamp.Path);
            Execute("INSERT INTO files(collection, path, size, mtime, hash) VALUES ($c, $p, $s, $m, $h)",
                ("$c", collection), ("$p", file.Stamp.Path), ("$s", file.Stamp.Size), ("$m", file.Stamp.ModifiedTicks), ("$h", file.Hash));
            foreach (var issue in file.Issues)
            {
                AddDiagnostic(collection, file.Stamp.Path, issue);
            }

            if (file.Entry is not null)
            {
                InsertEntry(collection, file);
            }
        }

        MarkDuplicates(collection);
        SetMeta("lastSync", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        transaction.Commit();
        return ValueTask.CompletedTask;
    }

    public ValueTask RetainCollectionsAsync(IReadOnlyCollection<CollectionId> collections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collections);
        var keep = collections.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var stale = new List<string>();
        using (var command = Command("SELECT DISTINCT collection FROM files UNION SELECT DISTINCT collection FROM entries"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!keep.Contains(reader.GetString(0)))
                {
                    stale.Add(reader.GetString(0));
                }
            }
        }

        using var transaction = _connection.BeginTransaction();
        foreach (var collection in stale)
        {
            Execute("DELETE FROM entry_fts WHERE rowid IN (SELECT rowid FROM entries WHERE collection = $c)", ("$c", collection));
            Execute("DELETE FROM entries WHERE collection = $c; DELETE FROM files WHERE collection = $c; DELETE FROM diagnostics WHERE collection = $c", ("$c", collection));
        }

        transaction.Commit();
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        Execute(IndexSchema.DropAll);
        EnsureSchema();
        return ValueTask.CompletedTask;
    }

    public ValueTask<CandidateSet> FindCandidatesAsync(CandidateQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var eligibility = Eligibility(query.Collections, query.Kinds, query.Filters, query.Include, query.Context, out var parameters, out var incompatible);
        var lexicalRanks = new Dictionary<long, int>();
        var candidateIds = new List<long>();

        var concepts = query.Terms.Concepts;
        if (concepts.Count > 0)
        {
            var match = string.Join(" OR ", concepts.Select(ConceptExpression));
            using var command = Command(
                $"SELECT e.rowid FROM entry_fts JOIN entries e ON e.rowid = entry_fts.rowid WHERE entry_fts MATCH $match AND {eligibility} " +
                $"ORDER BY bm25(entry_fts, {IndexSchema.Bm25Weights}) LIMIT $limit",
                [.. parameters, ("$match", match), ("$limit", query.Limit)]);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                lexicalRanks[id] = lexicalRanks.Count + 1;
                candidateIds.Add(id);
            }
        }

        foreach (var id in StructuralCandidates(query, eligibility, parameters))
        {
            if (!lexicalRanks.ContainsKey(id) && !candidateIds.Contains(id))
            {
                candidateIds.Add(id);
            }
        }

        var entries = LoadEntries(candidateIds);
        var hits = ConceptHits(concepts, candidateIds);
        var candidates = candidateIds
            .Where(entries.ContainsKey)
            .Select(id => new IndexCandidate(
                entries[id],
                lexicalRanks.TryGetValue(id, out var rank) ? rank : null,
                hits.TryGetValue(id, out var h) ? h : [],
                incompatible.Contains(id)))
            .ToList();

        var frequencies = concepts.Select(c => (int)Count($"SELECT count(*) FROM entry_fts WHERE entry_fts MATCH $m", ("$m", ConceptExpression(c)))).ToList();
        var total = Count("SELECT count(*) FROM entries WHERE duplicate = 0");
        return ValueTask.FromResult(new CandidateSet(candidates, frequencies, total));
    }

    public ValueTask<IndexedEntry?> FindAsync(CollectionId collection, EntryId id, CancellationToken cancellationToken)
    {
        using var command = Command($"SELECT {EntryColumns} FROM entries e WHERE collection = $c AND id = $i AND duplicate = 0", ("$c", collection.Value), ("$i", id.Value));
        using var reader = command.ExecuteReader();
        return ValueTask.FromResult(reader.Read() ? ReadEntry(reader).Entry : null);
    }

    public ValueTask<Page<IndexedEntry>> ListAsync(IndexListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var eligibility = Eligibility(query.Collections, query.Kinds, query.Filters, query.Include, QueryContext.Empty, out var parameters, out _);
        var sql = new StringBuilder($"SELECT {EntryColumns} FROM entries e WHERE {eligibility}");
        if (query.BlockingOnly)
        {
            sql.Append(" AND e.kind = 'uncertainty' AND e.blocking = 1 AND e.resolved = 0");
        }

        if (query.After is { } after)
        {
            sql.Append(" AND (e.collection > $ac OR (e.collection = $ac AND e.id > $ai))");
            parameters.Add(("$ac", after.Collection.Value));
            parameters.Add(("$ai", after.Entry.Value));
        }

        sql.Append(" ORDER BY e.collection, e.id LIMIT $n");
        parameters.Add(("$n", query.PageSize + 1));

        using var command = Command(sql.ToString(), [.. parameters]);
        using var reader = command.ExecuteReader();
        var items = new List<IndexedEntry>();
        while (reader.Read())
        {
            items.Add(ReadEntry(reader).Entry);
        }

        var hasMore = items.Count > query.PageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return ValueTask.FromResult(new Page<IndexedEntry>(items, hasMore ? items[^1].Ref.ToString() : null));
    }

    public ValueTask<IReadOnlyList<IndexedLink>> OutgoingLinksAsync(EntryRef source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var command = Command(
            "SELECT l.target_collection, l.target_entry, l.target_anchor, l.text FROM links l JOIN entries e ON e.rowid = l.source " +
            "WHERE e.collection = $c AND e.id = $i AND e.duplicate = 0 ORDER BY l.rowid",
            ("$c", source.Collection.Value), ("$i", source.Entry.Value));
        using var reader = command.ExecuteReader();
        var links = new List<IndexedLink>();
        while (reader.Read())
        {
            var target = new EntryRef(CollectionId.Parse(reader.GetString(0)), EntryId.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2));
            links.Add(new IndexedLink(source.WithoutAnchor(), target, reader.GetString(3)));
        }

        return ValueTask.FromResult<IReadOnlyList<IndexedLink>>(links);
    }

    public ValueTask<Page<IndexedLink>> BacklinksAsync(EntryRef target, EntryRef? after, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var sql = new StringBuilder(
            "SELECT DISTINCT e.collection, e.id, l.target_anchor, l.text FROM links l JOIN entries e ON e.rowid = l.source " +
            "WHERE l.target_collection = $c AND l.target_entry = $i AND e.duplicate = 0");
        var parameters = new List<(string, object?)> { ("$c", target.Collection.Value), ("$i", target.Entry.Value), ("$n", pageSize + 1) };
        if (after is not null)
        {
            sql.Append(" AND (e.collection > $ac OR (e.collection = $ac AND e.id > $ai))");
            parameters.Add(("$ac", after.Collection.Value));
            parameters.Add(("$ai", after.Entry.Value));
        }

        sql.Append(" ORDER BY e.collection, e.id LIMIT $n");
        using var command = Command(sql.ToString(), [.. parameters]);
        using var reader = command.ExecuteReader();
        var links = new List<IndexedLink>();
        while (reader.Read())
        {
            var source = new EntryRef(CollectionId.Parse(reader.GetString(0)), EntryId.Parse(reader.GetString(1)));
            links.Add(new IndexedLink(source, target with { Anchor = reader.IsDBNull(2) ? null : reader.GetString(2) }, reader.GetString(3)));
        }

        var hasMore = links.Count > pageSize;
        if (hasMore)
        {
            links.RemoveAt(links.Count - 1);
        }

        return ValueTask.FromResult(new Page<IndexedLink>(links, hasMore ? links[^1].Source.ToString() : null));
    }

    public ValueTask<IReadOnlyList<IndexDiagnostic>> DiagnosticsAsync(CancellationToken cancellationToken)
    {
        using var command = Command("SELECT collection, path, severity, code, field, message, line FROM diagnostics ORDER BY collection, path, rowid");
        using var reader = command.ExecuteReader();
        var diagnostics = new List<IndexDiagnostic>();
        while (reader.Read())
        {
            var severity = reader.GetString(2) == "error" ? IssueSeverity.Error : IssueSeverity.Warning;
            var issue = new ValidationIssue(severity, reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6));
            diagnostics.Add(new IndexDiagnostic(CollectionId.Parse(reader.GetString(0)), reader.GetString(1), issue));
        }

        return ValueTask.FromResult<IReadOnlyList<IndexDiagnostic>>(diagnostics);
    }

    public ValueTask<IndexStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var lastSync = GetMeta("lastSync") is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
        return ValueTask.FromResult(new IndexStatus(
            Count("SELECT count(*) FROM entries WHERE duplicate = 0"),
            Count("SELECT count(*) FROM files"),
            Count("SELECT count(*) FROM diagnostics"),
            IndexSchema.TokenizerClause(Tokenizer),
            lastSync));
    }

    private const string EntryColumns = "e.rowid, e.collection, e.id, e.revision, e.size, e.path, e.metadata, e.applicability";

    private void EnsureSchema()
    {
        var expected = $"{IndexSchema.Version}/{Tokenizer}";
        var hasMeta = Count("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'") == 1;
        if (hasMeta && GetMeta("schema") == expected)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        Execute(IndexSchema.DropAll);
        Execute(IndexSchema.Create(Tokenizer));
        SetMeta("schema", expected);
        transaction.Commit();
    }

    private void RemoveFile(string collection, string path)
    {
        // Look the row up first: FTS5 cannot use "rowid IN (subquery)" as a point lookup and would scan the table.
        using (var lookup = Command("SELECT rowid FROM entries WHERE collection = $c AND path = $p", ("$c", collection), ("$p", path)))
        {
            if (lookup.ExecuteScalar() is long rowid)
            {
                Execute("DELETE FROM entry_fts WHERE rowid = $r", ("$r", rowid));
                Execute("DELETE FROM entries WHERE rowid = $r", ("$r", rowid));
            }
        }

        Execute("DELETE FROM files WHERE collection = $c AND path = $p", ("$c", collection), ("$p", path));
        Execute("DELETE FROM diagnostics WHERE collection = $c AND path = $p", ("$c", collection), ("$p", path));
    }

    private void InsertEntry(string collection, ScannedFile file)
    {
        var entry = file.Entry!;
        var m = entry.Metadata;
        var a = entry.Applicability;
        var size = Encoding.UTF8.GetByteCount(entry.Body);
        using (var insert = Command(
            "INSERT INTO entries(collection, path, id, revision, kind, publication, retired, resolved, blocking, scope, size, metadata, applicability) " +
            "VALUES ($c, $p, $i, $r, $k, $pub, $ret, $res, $b, $s, $size, $m, $a)",
            ("$c", collection), ("$p", file.Stamp.Path), ("$i", m.Id.Value), ("$r", entry.Revision.Value),
            ("$k", WireNames.Kinds.Name(m.Kind)), ("$pub", WireNames.Publications.Name(m.Publication ?? Publication.Published)),
            ("$ret", m.IsRetired ? 1 : 0), ("$res", m.Resolution == Resolution.Resolved ? 1 : 0), ("$b", m.IsBlockingUncertainty ? 1 : 0),
            ("$s", WireNames.Scopes.Name(a.Scope)), ("$size", size),
            ("$m", MetadataJson.Serialize(EntryMetadataWriter.ToNode(m))), ("$a", ApplicabilityJson.Serialize(a))))
        {
            insert.ExecuteNonQuery();
        }

        var rowid = Count("SELECT last_insert_rowid()");
        foreach (var tag in m.Tags)
        {
            Execute("INSERT INTO entry_tags(entry, tag) VALUES ($e, $v)", ("$e", rowid), ("$v", tag));
        }

        foreach (var project in a.Projects)
        {
            Execute("INSERT INTO entry_projects(entry, project) VALUES ($e, $v)", ("$e", rowid), ("$v", project));
        }

        foreach (var symbol in m.Symbols)
        {
            Execute("INSERT INTO entry_symbols(entry, symbol) VALUES ($e, $v)", ("$e", rowid), ("$v", symbol.ToLowerInvariant()));
        }

        foreach (var package in a.Packages)
        {
            Execute("INSERT INTO entry_packages(entry, name, versions) VALUES ($e, $n, $v)", ("$e", rowid), ("$n", package.Name), ("$v", package.Versions));
        }

        foreach (var glob in m.Files)
        {
            Execute("INSERT INTO entry_files(entry, glob) VALUES ($e, $v)", ("$e", rowid), ("$v", glob));
        }

        foreach (var link in file.Analysis?.Links ?? [])
        {
            if (!EntryLink.HasScheme(link.Url))
            {
                continue;
            }

            if (EntryLink.TryParse(link.Url, out var target))
            {
                Execute("INSERT INTO links(source, target_collection, target_entry, target_anchor, text) VALUES ($e, $c, $i, $a, $t)",
                    ("$e", rowid), ("$c", target.Collection.Value), ("$i", target.Entry.Value), ("$a", target.Anchor), ("$t", link.Text));
            }
            else
            {
                AddDiagnostic(collection, file.Stamp.Path, ValidationIssue.Error(IssueCodes.InvalidReference, "body", $"Malformed entry link '{link.Url}'."));
            }
        }

        var identifiers = m.Symbols.SelectMany(s => new[] { s }.Concat(IdentifierSplitter.Split(s))).Distinct(StringComparer.Ordinal);
        Execute("INSERT INTO entry_fts(rowid, title, summary, tags, ident, body) VALUES ($e, $t, $s, $g, $i, $b)",
            ("$e", rowid), ("$t", m.Title), ("$s", m.Summary), ("$g", string.Join(' ', m.Tags.Select(t => t.Replace('-', ' ')))),
            ("$i", string.Join(' ', identifiers)), ("$b", entry.Body));
    }

    private void MarkDuplicates(string collection)
    {
        // Set-based and linear: group by ID once, keep the ordinally first path of each duplicated ID.
        Execute("UPDATE entries SET duplicate = 0 WHERE collection = $c AND duplicate = 1", ("$c", collection));
        Execute(
            "UPDATE entries SET duplicate = 1 WHERE rowid IN (" +
            "SELECT e.rowid FROM entries e JOIN (" +
            "SELECT id, MIN(path) AS keep FROM entries WHERE collection = $c GROUP BY id HAVING COUNT(*) > 1) d " +
            "ON e.id = d.id WHERE e.collection = $c AND e.path <> d.keep)",
            ("$c", collection));
        Execute("DELETE FROM diagnostics WHERE collection = $c AND code = $code", ("$c", collection), ("$code", DuplicateCode));
        using var command = Command("SELECT path, id FROM entries WHERE collection = $c AND duplicate = 1", ("$c", collection));
        using var reader = command.ExecuteReader();
        var duplicates = new List<(string Path, string Id)>();
        while (reader.Read())
        {
            duplicates.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (path, id) in duplicates)
        {
            AddDiagnostic(collection, path, ValidationIssue.Error(DuplicateCode, FieldNames.Id,
                $"Entry ID '{id}' is already used by another file in this collection; this file is ignored until the ID is changed."));
        }
    }

    private void AddDiagnostic(string collection, string path, ValidationIssue issue) =>
        Execute("INSERT INTO diagnostics(collection, path, severity, code, field, message, line) VALUES ($c, $p, $s, $code, $f, $m, $l)",
            ("$c", collection), ("$p", path), ("$s", issue.Severity == IssueSeverity.Error ? "error" : "warning"),
            ("$code", issue.Code), ("$f", issue.Field), ("$m", issue.Message), ("$l", issue.Line));

    /// <summary>Builds the eligibility predicate (plan §6.1 stage 2) and the set of version-incompatible entries.</summary>
    private string Eligibility(
        IReadOnlyList<CollectionId> collections,
        IReadOnlyList<EntryKind>? kinds,
        SearchFilters filters,
        InclusionOptions include,
        QueryContext context,
        out List<(string, object?)> parameters,
        out HashSet<long> incompatible)
    {
        parameters = [];
        var clauses = new List<string> { "e.duplicate = 0" };
        var allowed = filters.Collections is { Count: > 0 } chosen ? collections.Where(chosen.Contains).ToList() : [.. collections];
        clauses.Add(In("e.collection", allowed.Select(c => c.Value), "$col", parameters));

        if (filters.Kinds is { Count: > 0 } filterKinds)
        {
            kinds = filterKinds;
        }

        if (kinds is { Count: > 0 })
        {
            clauses.Add(In("e.kind", kinds.Select(WireNames.Kinds.Name), "$kind", parameters));
        }

        if (!include.Drafts)
        {
            clauses.Add("e.publication = 'published'");
        }

        if (!include.History)
        {
            clauses.Add("e.retired = 0 AND e.resolved = 0");
        }

        if (filters.Tags is { Count: > 0 } tags)
        {
            if (filters.TagMatch == TagMatch.All)
            {
                for (var i = 0; i < tags.Count; i++)
                {
                    clauses.Add($"EXISTS (SELECT 1 FROM entry_tags t WHERE t.entry = e.rowid AND t.tag = $tag{i})");
                    parameters.Add(($"$tag{i}", tags[i]));
                }
            }
            else
            {
                clauses.Add($"EXISTS (SELECT 1 FROM entry_tags t WHERE t.entry = e.rowid AND {In("t.tag", tags, "$tag", parameters)})");
            }
        }

        incompatible = IncompatibleEntries(context);
        if (!include.Incompatible && incompatible.Count > 0)
        {
            clauses.Add($"e.rowid NOT IN ({string.Join(',', incompatible.Select(id => id.ToString(CultureInfo.InvariantCulture)))})");
        }

        return "(" + string.Join(" AND ", clauses) + ")";
    }

    private HashSet<long> IncompatibleEntries(QueryContext context)
    {
        var result = new HashSet<long>();
        if (context.Packages is not { Count: > 0 } packages)
        {
            return result;
        }

        var parameters = new List<(string, object?)>();
        using var command = Command($"SELECT entry, name, versions FROM entry_packages WHERE {In("name", packages.Keys, "$pkg", parameters)}", [.. parameters]);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (VersionRange.Check(reader.GetString(2), packages[reader.GetString(1)]) == VersionCompatibility.Incompatible)
            {
                result.Add(reader.GetInt64(0));
            }
        }

        return result;
    }

    private List<long> StructuralCandidates(CandidateQuery query, string eligibility, List<(string, object?)> parameters)
    {
        var ids = new List<long>();
        var context = query.Context;

        void Collect(string sql, IEnumerable<(string, object?)> extra)
        {
            using var command = Command(sql, [.. parameters, .. extra]);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        if (context.Symbols is { Count: > 0 } symbols)
        {
            var extra = new List<(string, object?)>();
            var symbolIn = In("s.symbol", symbols.Select(s => s.ToLowerInvariant()), "$sym", extra);
            Collect($"SELECT DISTINCT e.rowid FROM entry_symbols s JOIN entries e ON e.rowid = s.entry WHERE {symbolIn} AND {eligibility}", extra);
        }

        if (context.Packages is { Count: > 0 } packages)
        {
            var extra = new List<(string, object?)>();
            var packageIn = In("p.name", packages.Keys, "$spk", extra);
            Collect($"SELECT DISTINCT e.rowid FROM entry_packages p JOIN entries e ON e.rowid = p.entry WHERE {packageIn} AND {eligibility}", extra);
        }

        if (context.Files is { Count: > 0 } files)
        {
            using var command = Command($"SELECT e.rowid, f.glob FROM entry_files f JOIN entries e ON e.rowid = f.entry WHERE {eligibility}", [.. parameters]);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (GlobPattern.TryParse(reader.GetString(1), out var glob) && files.Any(glob.IsMatch))
                {
                    ids.Add(reader.GetInt64(0));
                }
            }
        }

        // Blocking uncertainties of this project (or general ones) are always candidates (A4); ranking decides whether they surface.
        var project = context.ProjectId ?? "";
        Collect(
            $"SELECT e.rowid FROM entries e WHERE e.kind = 'uncertainty' AND e.blocking = 1 AND e.resolved = 0 AND " +
            $"(e.scope = 'general' OR EXISTS (SELECT 1 FROM entry_projects p WHERE p.entry = e.rowid AND p.project = $project)) AND {eligibility}",
            [("$project", project)]);

        return [.. ids.Distinct()];
    }

    private Dictionary<long, IndexedEntry> LoadEntries(List<long> ids)
    {
        var result = new Dictionary<long, IndexedEntry>();
        if (ids.Count == 0)
        {
            return result;
        }

        using var command = Command($"SELECT {EntryColumns} FROM entries e WHERE e.rowid IN ({string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)))})");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var (rowid, entry) = ReadEntry(reader);
            result[rowid] = entry;
        }

        return result;
    }

    private Dictionary<long, List<ConceptHit>> ConceptHits(IReadOnlyList<QueryConcept> concepts, List<long> ids)
    {
        var hits = new Dictionary<long, List<ConceptHit>>();
        if (concepts.Count == 0 || ids.Count == 0)
        {
            return hits;
        }

        var idList = string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        for (var i = 0; i < concepts.Count; i++)
        {
            var expression = ConceptExpression(concepts[i]);
            var descriptive = RowIds($"SELECT rowid FROM entry_fts WHERE entry_fts MATCH $m AND rowid IN ({idList})", IndexSchema.DescriptiveColumns + " : " + expression);
            var any = RowIds($"SELECT rowid FROM entry_fts WHERE entry_fts MATCH $m AND rowid IN ({idList})", expression);
            foreach (var id in any)
            {
                if (!hits.TryGetValue(id, out var list))
                {
                    hits[id] = list = [];
                }

                list.Add(new ConceptHit(i, descriptive.Contains(id)));
            }
        }

        return hits;
    }

    private HashSet<long> RowIds(string sql, string match)
    {
        using var command = Command(sql, ("$m", match));
        using var reader = command.ExecuteReader();
        var ids = new HashSet<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>A concept as an FTS expression: every term quoted (multi-word terms become phrases), OR-combined.</summary>
    private static string ConceptExpression(QueryConcept concept) =>
        "(" + string.Join(" OR ", concept.Terms.Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")) + ")";

    private static (long RowId, IndexedEntry Entry) ReadEntry(SqliteDataReader reader)
    {
        var collection = CollectionId.Parse(reader.GetString(1));
        var metadata = EntryMetadataReader.Read((MetadataMapping)MetadataJson.Deserialize(reader.GetString(6))).Metadata
            ?? throw new InvalidOperationException("Index holds metadata that no longer validates; run 'brain index reset'.");
        var entry = new IndexedEntry(
            new EntryRef(collection, EntryId.Parse(reader.GetString(2))),
            metadata,
            ApplicabilityJson.Deserialize(reader.GetString(7)),
            Revision.Parse(reader.GetString(3)),
            reader.GetInt32(4),
            reader.GetString(5));
        return (reader.GetInt64(0), entry);
    }

    private static string In(string column, IEnumerable<string> values, string prefix, List<(string, object?)> parameters)
    {
        var names = new List<string>();
        foreach (var value in values)
        {
            var name = prefix + parameters.Count.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            parameters.Add((name, value));
        }

        return names.Count == 0 ? "0" : $"{column} IN ({string.Join(',', names)})";
    }

    private string? GetMeta(string key)
    {
        using var command = Command("SELECT value FROM meta WHERE key = $k", ("$k", key));
        return command.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value) =>
        Execute("INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    private long Count(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private int Count(string sql) => (int)Count(sql, []);

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
