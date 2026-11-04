namespace AgentBrain.Infrastructure.Sqlite;

/// <summary>FTS5 tokenizer variants (plan §5.7). Which one is the default is a calibration item.</summary>
public enum IndexTokenizer
{
    /// <summary><c>porter unicode61 remove_diacritics 2</c>: English stemming.</summary>
    Porter,

    /// <summary><c>unicode61 remove_diacritics 2</c>: no stemming.</summary>
    Unicode61,
}

/// <summary>
/// The derived index schema (plan §7.5). The index is rebuildable: a schema or tokenizer mismatch drops
/// and recreates everything instead of migrating.
/// </summary>
internal static class IndexSchema
{
    public const string Version = "3";

    /// <summary>BM25 column weights for (title, summary, tags, ident, body). Calibration item.</summary>
    public const string Bm25Weights = "10.0, 6.0, 6.0, 4.0, 1.0";

    public const string DescriptiveColumns = "{title summary tags ident}";

    public static string TokenizerClause(IndexTokenizer tokenizer) => tokenizer switch
    {
        IndexTokenizer.Porter => "porter unicode61 remove_diacritics 2",
        IndexTokenizer.Unicode61 => "unicode61 remove_diacritics 2",
        _ => throw new ArgumentOutOfRangeException(nameof(tokenizer)),
    };

    public static string Create(IndexTokenizer tokenizer) => $"""
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE files(
            collection TEXT NOT NULL, path TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, hash TEXT NOT NULL,
            PRIMARY KEY (collection, path));
        CREATE TABLE entries(
            rowid INTEGER PRIMARY KEY,
            collection TEXT NOT NULL, path TEXT NOT NULL, id TEXT NOT NULL,
            revision TEXT NOT NULL, kind TEXT NOT NULL, publication TEXT NOT NULL,
            retired INTEGER NOT NULL, resolved INTEGER NOT NULL, blocking INTEGER NOT NULL,
            scope TEXT NOT NULL, size INTEGER NOT NULL, duplicate INTEGER NOT NULL DEFAULT 0,
            metadata TEXT NOT NULL, applicability TEXT NOT NULL,
            UNIQUE (collection, path));
        CREATE INDEX entries_identity ON entries(collection, id);
        CREATE TABLE entry_tags(entry INTEGER NOT NULL REFERENCES entries(rowid) ON DELETE CASCADE, tag TEXT NOT NULL);
        CREATE INDEX entry_tags_tag ON entry_tags(tag, entry);
        CREATE INDEX entry_tags_entry ON entry_tags(entry);
        CREATE TABLE entry_projects(entry INTEGER NOT NULL REFERENCES entries(rowid) ON DELETE CASCADE, project TEXT NOT NULL);
        CREATE INDEX entry_projects_project ON entry_projects(project, entry);
        CREATE INDEX entry_projects_entry ON entry_projects(entry);
        CREATE TABLE entry_symbols(entry INTEGER NOT NULL REFERENCES entries(rowid) ON DELETE CASCADE, symbol TEXT NOT NULL);
        CREATE INDEX entry_symbols_symbol ON entry_symbols(symbol, entry);
        CREATE INDEX entry_symbols_entry ON entry_symbols(entry);
        CREATE TABLE entry_packages(entry INTEGER NOT NULL REFERENCES entries(rowid) ON DELETE CASCADE, name TEXT NOT NULL, versions TEXT NOT NULL);
        CREATE INDEX entry_packages_name ON entry_packages(name, entry);
        CREATE INDEX entry_packages_entry ON entry_packages(entry);
        CREATE TABLE entry_files(entry INTEGER NOT NULL REFERENCES entries(rowid) ON DELETE CASCADE, glob TEXT NOT NULL);
        CREATE INDEX entry_files_entry ON entry_files(entry);
        CREATE TABLE links(
            source INTEGER NOT NULL REFERENCES entries(rowid) ON DELETE CASCADE,
            target_collection TEXT NOT NULL, target_entry TEXT NOT NULL, target_anchor TEXT, text TEXT NOT NULL);
        CREATE INDEX links_target ON links(target_collection, target_entry);
        CREATE INDEX links_source ON links(source);
        CREATE TABLE diagnostics(
            collection TEXT NOT NULL, path TEXT NOT NULL, severity TEXT NOT NULL, code TEXT NOT NULL,
            field TEXT NOT NULL, message TEXT NOT NULL, line INTEGER);
        CREATE INDEX diagnostics_file ON diagnostics(collection, path);
        CREATE VIRTUAL TABLE entry_fts USING fts5(title, summary, tags, ident, body, tokenize = '{TokenizerClause(tokenizer)}');
        """;

    public const string DropAll = """
        DROP TABLE IF EXISTS entry_fts;
        DROP TABLE IF EXISTS links;
        DROP TABLE IF EXISTS entry_files;
        DROP TABLE IF EXISTS entry_packages;
        DROP TABLE IF EXISTS entry_symbols;
        DROP TABLE IF EXISTS entry_projects;
        DROP TABLE IF EXISTS entry_tags;
        DROP TABLE IF EXISTS diagnostics;
        DROP TABLE IF EXISTS entries;
        DROP TABLE IF EXISTS files;
        DROP TABLE IF EXISTS meta;
        """;
}
