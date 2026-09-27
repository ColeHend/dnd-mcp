namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// The srd.db schema and its version.
///
/// <para>
/// srd.db is a disposable cache of the vendored content, never migrated: when anything about how it is built changes,
/// <see cref="Version"/> changes, the staleness key (<see cref="SrdIndexContent.ComputeStalenessKey"/>) changes with it,
/// and every server rebuilds the file on its next start. So bump <see cref="Version"/> on ANY change to the tables below,
/// to the importer, to the search text (<see cref="SrdSearchText"/>), to name keys (<see cref="SrdNames.Key"/>), to
/// aliases or to counterparts (<see cref="SrdCounterparts"/>). Forgetting means an index built by the old code is reused
/// by the new code, which then queries columns or relies on rows that are not there, or silently answers with the old
/// pairing rules. The key cannot see code, only this number; <c>SrdIndexSchemaVersionTests</c> pins a digest of the
/// hand-written tables (the DDL, <see cref="SrdCounterparts"/>' manual lists and <see cref="SrdCuratedAliases"/>' lists)
/// to the version, so editing any of them without a bump fails a test that says so. Logic changes (a new pairing rule, a new alias source) still rely on the
/// author: the per-source totals in <c>SrdCounterpartTests</c> move with them, and that diff is the prompt to bump.
/// </para>
/// <para>
/// Version history: 1, Phase 2. 2: curated corrections (<c>doc.corrections</c>), counterpart sources <c>section</c>,
/// <c>feature</c>, <c>variant</c> and <c>level</c>, level records paired by slug, qualifier aliases for every kind,
/// <c>heading</c> aliases, and the corrections file in the staleness key. 3: heading aliases only from the curated list
/// (<see cref="SrdCuratedAliases"/>), alias sources <c>section</c> and <c>curated</c>, <c>alias.position</c> (the order
/// the builder chose), and <c>doc.level</c> filled for features.
/// </para>
/// <para>
/// Design points the queries depend on:
/// <list type="bullet">
/// <item>STRICT tables, so a string in an integer column (a level, a flag) is an error at build time instead of a
/// comparison that never matches at query time.</item>
/// <item><c>doc_fts</c> is an external-content FTS5 table over <c>doc(name, aliases, text)</c>. It holds only
/// <c>searchable</c> rows: level records are reachable by ref but would bury real results under 577 rows named
/// "Fighter 5", "Wizard 12" and so on.</item>
/// <item><c>doc.json</c> is the record AFTER any curated correction (<see cref="SrdCorrections"/>), and
/// <c>doc.corrections</c> holds the reasons, one per line (empty for an untouched record), so a corrected record is
/// never shown as if it were the upstream text.</item>
/// <item><c>counterpart</c> pairs a 2014 document with a 2024 document and is many-to-many (2014 "Succubus/Incubus" is
/// 2024 "Succubus" and "Incubus"; seven 2014 "Ability Score Improvement" features are the one 2024 feature).</item>
/// <item><c>alias</c> keeps the name keys other names answer to, with where each came from
/// (<see cref="SrdAliasSources"/>) and its <c>position</c> among the document's aliases (the other edition's graded
/// names in level order: "Bardic Inspiration (d6), (d8), (d10), (d12)"); the same names, newline-separated, fill
/// <c>doc.aliases</c> so FTS can match them too.</item>
/// <item><c>doc.level</c> is a level record's level and a feature's class level, so graded features sort by the order
/// a class gains them.</item>
/// <item><c>PRAGMA user_version</c> holds <see cref="Version"/>, readable without trusting any table to exist.</item>
/// </list>
/// </para>
/// </summary>
public static class SrdIndexSchema
{
    /// <summary>Bump on any change listed in the type summary. Stored as <c>PRAGMA user_version</c> and in <c>meta</c>.</summary>
    public const int Version = 3;

    public const string MetaStalenessKey = "staleness_key";
    public const string MetaSchemaVersion = "schema_version";
    public const string MetaContentTag = "content_tag";
    public const string MetaContentFingerprint = "content_fingerprint";
    public const string MetaGlossarySha256 = "glossary_sha256";
    public const string MetaBuiltAtUtc = "built_at_utc";
    public const string MetaDocumentCount = "document_count";
    public const string MetaCorrectionsSha256 = "corrections_sha256";

    /// <summary>Prefix of the per-(edition, kind) count keys: <c>count:2014/monster</c>.</summary>
    public const string MetaCountPrefix = "count:";

    /// <summary>Same kind (races as species) and same slug. Level records included: <c>fighter-5</c> is Fighter 5 in both.</summary>
    public const string CounterpartBySlug = "slug";

    /// <summary>Same kind and the same name, unique on both sides (features: unique within their class).</summary>
    public const string CounterpartByName = "name";

    /// <summary>A hand-verified rename in <see cref="SrdCounterparts.Manual"/>, possibly across kinds.</summary>
    public const string CounterpartManual = "manual";

    /// <summary>
    /// A 2024 glossary entry (or poison) and the 2014 rules section whose text covers it
    /// (<see cref="SrdCounterparts.Sections"/>): not a rename, so neither name becomes an alias of the other.
    /// </summary>
    public const string CounterpartBySection = "section";

    /// <summary>
    /// Features of one class with the same base name once the 2014 grade is stripped ("Wild Shape (CR 1 or below)",
    /// "Spellcasting: Wizard", the seven "Ability Score Improvement" records): every 2014 grade pairs with the 2024 feature.
    /// </summary>
    public const string CounterpartByFeature = "feature";

    /// <summary>A 2014 variant magic item (Belt of Hill Giant Strength) and whatever its 2014 parent pairs with.</summary>
    public const string CounterpartByVariant = "variant";

    /// <summary>A subclass level whose subclass was renamed (2014 <c>draconic-6</c>, 2024 <c>draconic-sorcery-6</c>).</summary>
    public const string CounterpartByLevel = "level";

    /// <summary>Where an alias came from. The wire strings are <see cref="SrdAliasSources"/>'.</summary>
    public const string AliasFromQualifier = SrdAliasSources.Qualifier;
    public const string AliasFromCounterpart = SrdAliasSources.Counterpart;
    public const string AliasFromHeading = SrdAliasSources.Heading;
    public const string AliasFromSection = SrdAliasSources.Section;
    public const string AliasFromCurated = SrdAliasSources.Curated;

    internal const string CreateTables = """
        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) STRICT;

        CREATE TABLE doc (
            id            INTEGER PRIMARY KEY,
            edition       TEXT    NOT NULL CHECK (edition IN ('2014', '2024')),
            kind          TEXT    NOT NULL,
            slug          TEXT    NOT NULL CHECK (slug <> ''),
            name          TEXT    NOT NULL CHECK (name <> ''),
            name_key      TEXT    NOT NULL,
            aliases       TEXT    NOT NULL,
            text          TEXT    NOT NULL,
            json          TEXT    NOT NULL CHECK (json_valid(json)),
            class_slug    TEXT,
            subclass_slug TEXT,
            level         INTEGER,
            searchable    INTEGER NOT NULL CHECK (searchable IN (0, 1)),
            corrections   TEXT    NOT NULL DEFAULT '',
            UNIQUE (edition, kind, slug)
        ) STRICT;

        CREATE INDEX doc_by_name_key ON doc (edition, name_key);
        CREATE INDEX doc_by_class_level ON doc (edition, class_slug, level) WHERE kind = 'level';
        CREATE INDEX doc_by_subclass_level ON doc (edition, subclass_slug, level) WHERE kind = 'level';

        CREATE TABLE alias (
            doc_id    INTEGER NOT NULL REFERENCES doc (id),
            alias     TEXT    NOT NULL,
            alias_key TEXT    NOT NULL CHECK (alias_key <> ''),
            source    TEXT    NOT NULL CHECK (source IN ('qualifier', 'counterpart', 'heading', 'section', 'curated')),
            position  INTEGER NOT NULL,
            PRIMARY KEY (doc_id, alias_key)
        ) STRICT, WITHOUT ROWID;

        CREATE INDEX alias_by_key ON alias (alias_key);

        CREATE TABLE counterpart (
            doc_2014 INTEGER NOT NULL REFERENCES doc (id),
            doc_2024 INTEGER NOT NULL REFERENCES doc (id),
            source   TEXT    NOT NULL CHECK (source IN ('slug', 'name', 'manual', 'section', 'feature', 'variant', 'level')),
            PRIMARY KEY (doc_2014, doc_2024)
        ) STRICT, WITHOUT ROWID;

        CREATE INDEX counterpart_by_2024 ON counterpart (doc_2024);

        CREATE VIRTUAL TABLE doc_fts USING fts5(
            name, aliases, text,
            content = 'doc', content_rowid = 'id',
            tokenize = 'porter unicode61 remove_diacritics 2');
        """;
}
