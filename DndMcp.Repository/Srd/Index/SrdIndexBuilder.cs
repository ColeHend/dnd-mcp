using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// Builds srd.db from the vendored content: every record of every kind in both editions, plus the 2024 Rules Glossary,
/// with name keys, aliases, 2014↔2024 counterparts and the full-text index.
///
/// <para>
/// <b>Refuses rather than guesses.</b> Every file the 5e-database manifest pins must be present and unchanged
/// (<see cref="ContentManifestVerifier"/>; the refusal carries <see cref="ContentVerificationResult.Describe"/>), the
/// glossary and the corrections must be readable, and every record must have the fields an index row needs (string
/// <c>index</c> and <c>url</c>, a name; glossary records a <c>name</c>). A record without them fails the build with its
/// file and position; skipping it would make a spell silently unfindable. Files the manifest does not list are only
/// warned about: the builder reads nothing but pinned files, so a stray one cannot change the index, and the usual
/// stray is a previous version's directory that <c>dotnet publish</c> left in the install.
/// </para>
/// <para>
/// <b>Corrected, and labelled.</b> Each record passes through <see cref="SrdCorrections"/> before anything reads it:
/// the corrected JSON is what is stored, searched and name-keyed, and the reason goes into <c>doc.corrections</c>. A
/// correction the content cannot take (its record is gone, or it sets a property the record lacks) is skipped with a
/// warning, like a manual counterpart whose record is gone; the tests of the shipped file catch that before release.
/// </para>
/// <para>
/// <b>Never exposes a half-built file.</b> The build writes <c>srd.db.&lt;pid&gt;.&lt;guid&gt;.tmp</c> beside the target
/// and renames it over the target only once it is complete and closed. Several servers (one per Claude session) can
/// build at once: each writes its own temp file and the last rename wins. From the same content every build is the
/// same index. A server already running keeps reading the file it validated, even after a rename replaces it (see
/// <see cref="SrdIndex"/>: every connection is opened up front). A failed build deletes its temp file and leaves any
/// existing srd.db untouched.
/// </para>
/// <para>
/// <b>journal_mode DELETE, never WAL.</b> Readers open srd.db read-only. A WAL database needs its -wal and -shm files
/// (and write access to create them) even to be read, and a rename moves only the main file.
/// </para>
/// </summary>
public static partial class SrdIndexBuilder
{
    private static readonly JsonWriterOptions CompactJson = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Builds srd.db at <paramref name="targetPath"/> from the content under <paramref name="contentRoot"/>.</summary>
    /// <exception cref="SrdIndexUnavailableException">The content is missing, damaged or has an unusable record.</exception>
    public static SrdIndexBuildSummary Build(string contentRoot, string targetPath) =>
        Build(SrdIndexContent.Load(contentRoot), targetPath);

    /// <summary>Builds srd.db at <paramref name="targetPath"/> from already-loaded <paramref name="content"/>.</summary>
    /// <exception cref="SrdIndexUnavailableException">The content is missing, damaged or has an unusable record.</exception>
    /// <exception cref="IOException">The target directory cannot be written, or the finished file cannot be moved into place.</exception>
    public static SrdIndexBuildSummary Build(SrdIndexContent content, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var stopwatch = Stopwatch.StartNew();
        var verification = VerifyTree(content);
        if (verification.Missing.Count > 0 || verification.Mismatched.Count > 0)
        {
            throw new SrdIndexUnavailableException($"{verification.Describe()} {ReinstallHint}");
        }

        var warnings = new List<string>();
        if (verification.Extra.Count > 0)
        {
            warnings.Add(
                $"Ignored {verification.Extra.Count.ToString(CultureInfo.InvariantCulture)} file(s) under {content.DatabaseDirectory} " +
                $"that are not in the content manifest ({string.Join(", ", verification.Extra)}); the index reads only the " +
                "files the manifest pins. Delete them, or publish into an empty directory, to silence this warning.");
        }

        var target = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        DeleteAbandonedTempFiles(directory, Path.GetFileName(target));

        var tempPath = Path.Combine(
            directory,
            $"{Path.GetFileName(target)}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.{Guid.NewGuid():N}.tmp");
        try
        {
            SrdIndexBuildSummary summary;
            // Pooling=False: a pooled connection would keep the temp file open after Dispose, and on Windows an open
            // file cannot be renamed.
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                summary = Write(connection, content, warnings);
            }

            File.Move(tempPath, target, overwrite: true);
            return summary with { Elapsed = stopwatch.Elapsed };
        }
        catch
        {
            // The connection is closed by now, which rolled back and deleted any journal; only the database remains.
            DeleteQuietly(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Appended to a refusal over missing or changed vendored files. The verifier's own advice (re-vendor with the
    /// script) is for a source checkout; an installed copy has no scripts, only its publish output to copy again.
    /// </summary>
    internal const string ReinstallHint = "In an installed copy, publish or copy the whole content/ directory again.";

    private static ContentVerificationResult VerifyTree(SrdIndexContent content)
    {
        try
        {
            return new ContentManifestVerifier().Verify(content.DatabaseDirectory, content.Manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SrdIndexUnavailableException(
                $"Cannot read the vendored content under {content.DatabaseDirectory}: {ex.Message}", ex);
        }
    }

    private static SrdIndexBuildSummary Write(SqliteConnection connection, SrdIndexContent content, List<string> warnings)
    {
        Execute(connection, "PRAGMA journal_mode = DELETE;");
        Execute(connection, SrdIndexSchema.CreateTables);

        using var transaction = connection.BeginTransaction();
        var documents = new List<IndexedDocument>();
        var seen = new Dictionary<(string Edition, string Kind, string Slug), string>();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO doc (edition, kind, slug, name, name_key, aliases, text, json, class_slug, subclass_slug, level, searchable, corrections)
                VALUES ($edition, $kind, $slug, $name, $name_key, '', $text, $json, $class_slug, $subclass_slug, $level, $searchable, $corrections)
                RETURNING id;
                """;
            var parameters = new[] { "$edition", "$kind", "$slug", "$name", "$name_key", "$text", "$json", "$class_slug", "$subclass_slug", "$level", "$searchable", "$corrections" }
                .ToDictionary(n => n, n => insert.Parameters.Add(new SqliteParameter { ParameterName = n }));

            void Insert(SourceRecord record)
            {
                var key = (record.Edition, record.Kind, record.Slug);
                var position = $"{record.File} at $[{record.Position}]";
                if (seen.TryGetValue(key, out var firstPosition))
                {
                    throw new SrdIndexUnavailableException(
                        $"{position}: slug \"{record.Slug}\" repeats {firstPosition}; every {record.Kind} needs its own slug. " +
                        (record.File == SrdKinds.RulesGlossary2024FileName
                            ? "Restore content/rules-glossary-2024.json from the repository."
                            : "Re-vendor with scripts/fetch-5e-database.sh."));
                }

                seen.Add(key, position);
                var text = SrdSearchText.Build(record.Element);
                parameters["$edition"].Value = record.Edition;
                parameters["$kind"].Value = record.Kind;
                parameters["$slug"].Value = record.Slug;
                parameters["$name"].Value = record.Name;
                parameters["$name_key"].Value = SrdNames.Key(record.Name);
                parameters["$text"].Value = text;
                parameters["$json"].Value = Compact(record.Element);
                parameters["$class_slug"].Value = (object?)record.ClassSlug ?? DBNull.Value;
                parameters["$subclass_slug"].Value = (object?)record.SubclassSlug ?? DBNull.Value;
                parameters["$level"].Value = (object?)record.Level ?? DBNull.Value;
                parameters["$searchable"].Value = record.Kind == SrdKinds.Level ? 0 : 1;
                parameters["$corrections"].Value = record.CorrectionReason ?? string.Empty;
                var id = (long)insert.ExecuteScalar()!;

                var isOldRule = record.Kind == SrdKinds.Rule && record.Edition == SrdEdition.Edition2014;
                documents.Add(new IndexedDocument(
                    id, record.Edition, record.Kind, record.Slug, record.Name, SrdNames.Key(record.Name),
                    record.Kind == SrdKinds.Feature ? ClassIndexOf(record.Element) : null)
                {
                    SubclassSlug = record.SubclassSlug,
                    Level = record.Level,
                    Variants = record.Kind == SrdKinds.MagicItem ? VariantSlugs(record.Element) : [],
                    Headings = isOldRule ? SubsectionHeadings(record.Element) : [],
                    TextKey = isOldRule ? SrdNames.Key(text) : string.Empty,
                });
            }

            foreach (var edition in SrdEdition.All)
            {
                foreach (var kind in SrdKinds.All)
                {
                    if (kind.FileFor(edition) is { } fileName)
                    {
                        var relativePath = ManifestPath(content, edition, fileName);
                        foreach (var record in ReadVendoredFile(content, relativePath, edition, kind.Name, warnings))
                        {
                            Insert(record);
                        }
                    }
                    else if (kind.Name == SrdKinds.Rule && edition == SrdEdition.Edition2024)
                    {
                        foreach (var record in ReadGlossary(content, warnings))
                        {
                            Insert(record);
                        }
                    }
                }
            }
        }

        foreach (var correction in content.Corrections.Entries.Where(c => !seen.ContainsKey((c.Target.Edition, c.Target.Kind, c.Target.Slug))))
        {
            warnings.Add($"Correction for {correction.Ref} skipped: the content has no such record (content/{SrdCorrections.FileName}).");
        }

        var pairs = SrdCounterparts.Match(documents, warnings);
        var aliases = BuildAliases(documents, pairs, warnings);
        WriteAliases(connection, transaction, aliases);
        WriteCounterparts(connection, transaction, pairs);

        Execute(connection, "INSERT INTO doc_fts (rowid, name, aliases, text) SELECT id, name, aliases, text FROM doc WHERE searchable = 1;", transaction);

        var counts = documents
            .GroupBy(d => (d.Edition, d.Kind))
            .OrderBy(g => g.Key.Edition, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Kind, StringComparer.Ordinal)
            .Select(g => new SrdKindCount(g.Key.Edition, g.Key.Kind, g.Count()))
            .ToList();
        var builtAt = DateTimeOffset.UtcNow;
        var meta = new List<(string Key, string Value)>
        {
            (SrdIndexSchema.MetaStalenessKey, content.StalenessKey),
            (SrdIndexSchema.MetaSchemaVersion, SrdIndexSchema.Version.ToString(CultureInfo.InvariantCulture)),
            (SrdIndexSchema.MetaContentTag, content.Manifest.Tag),
            (SrdIndexSchema.MetaContentFingerprint, content.ContentFingerprint),
            (SrdIndexSchema.MetaGlossarySha256, content.GlossarySha256),
            (SrdIndexSchema.MetaCorrectionsSha256, content.CorrectionsSha256),
            (SrdIndexSchema.MetaBuiltAtUtc, builtAt.ToString("O", CultureInfo.InvariantCulture)),
            (SrdIndexSchema.MetaDocumentCount, documents.Count.ToString(CultureInfo.InvariantCulture)),
        };
        meta.AddRange(counts.Select(c => (
            $"{SrdIndexSchema.MetaCountPrefix}{c.Edition}/{c.Kind}", c.Count.ToString(CultureInfo.InvariantCulture))));
        using (var insertMeta = connection.CreateCommand())
        {
            insertMeta.Transaction = transaction;
            insertMeta.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value);";
            var key = insertMeta.Parameters.Add(new SqliteParameter { ParameterName = "$key" });
            var value = insertMeta.Parameters.Add(new SqliteParameter { ParameterName = "$value" });
            foreach (var (k, v) in meta)
            {
                key.Value = k;
                value.Value = v;
                insertMeta.ExecuteNonQuery();
            }
        }

        Execute(connection, $"PRAGMA user_version = {SrdIndexSchema.Version.ToString(CultureInfo.InvariantCulture)};", transaction);
        transaction.Commit();
        Execute(connection, "INSERT INTO doc_fts (doc_fts) VALUES ('optimize');");

        return new SrdIndexBuildSummary(content.StalenessKey, documents.Count, counts, warnings, builtAt, TimeSpan.Zero);
    }

    /// <summary>
    /// Aliases per document, in this order (kept as <c>alias.position</c>), deduplicated by name key and never the
    /// document's own name:
    /// <list type="number">
    /// <item><c>qualifier</c>: any name's bare form without its trailing parenthetical ("Finesse (Weapon Property)" →
    /// "Finesse", "Wild Shape (CR 1 or below)" → "Wild Shape", "Oil (flask)" → "Oil"). Level names are generated and
    /// never qualified.</item>
    /// <item><c>heading</c>: a 2014 rule's <c>####</c>/<c>#####</c> subsection headings that
    /// <see cref="SrdCuratedAliases.Headings"/> lists, in text order (Melee Attacks answers to "Grappling"; Creating
    /// Sentient Magic Items does not answer to "Alignment").</item>
    /// <item><c>section</c>: the names of the 2024 entries <see cref="SrdCounterparts.Sections"/> pairs with a 2014
    /// section, where the section's text uses the name and no heading already gave it (Melee Attacks answers to
    /// "Unarmed Strike", Damage Rolls to the glossary's singular "Critical Hit").</item>
    /// <item><c>curated</c>: <see cref="SrdCuratedAliases.Names"/> ("Shove", "Damage Resistance").</item>
    /// <item><c>counterpart</c>: each counterpart's name, both ways, for every pair but section pairs (not renames)
    /// and level pairs (generated names), lowest class level first, so 2024 Bardic Inspiration's 2014 names read d6, d8,
    /// d10, d12.</item>
    /// </list>
    /// Which match wins when several documents answer to one name is the query's business (<see cref="SrdIndex"/>'s
    /// name-match tiers): a bare name another document owns (2014 "Acid (vial)" → "Acid", the damage type's own name)
    /// makes this document one more match of that name, ranked by those tiers. Curated entries whose document or heading
    /// the content lacks are skipped with a warning.
    /// </summary>
    private static Dictionary<long, List<(string Alias, string Source)>> BuildAliases(
        IReadOnlyList<IndexedDocument> documents,
        IReadOnlyList<CounterpartPair> pairs,
        List<string> warnings)
    {
        var aliases = new Dictionary<long, List<(string Alias, string Source)>>();
        var keys = new Dictionary<long, HashSet<string>>();

        void Add(IndexedDocument doc, string alias, string source)
        {
            var key = SrdNames.Key(alias);
            if (key.Length == 0 || key == doc.NameKey)
            {
                return;
            }

            if (!keys.TryGetValue(doc.Id, out var known))
            {
                keys[doc.Id] = known = new HashSet<string>(StringComparer.Ordinal);
                aliases[doc.Id] = [];
            }

            if (known.Add(key))
            {
                aliases[doc.Id].Add((alias, source));
            }
        }

        foreach (var doc in documents.Where(d => d.Kind != SrdKinds.Level))
        {
            if (SrdCounterparts.BareName(doc.Name) is { } bare)
            {
                Add(doc, bare, SrdIndexSchema.AliasFromQualifier);
            }
        }

        var byRef = documents.ToDictionary(d => (d.Edition, d.Kind, d.Slug));
        var curatedHeadings = new HashSet<(string Slug, string Heading)>();
        foreach (var entry in SrdCuratedAliases.Headings)
        {
            if (!byRef.TryGetValue((SrdEdition.Edition2014, SrdKinds.Rule, entry.Slug), out var rule))
            {
                warnings.Add($"Curated heading alias {entry} skipped: the content has no 2014 rule \"{entry.Slug}\".");
            }
            else if (!rule.Headings.Contains(entry.Heading, StringComparer.Ordinal))
            {
                warnings.Add($"Curated heading alias {entry} skipped: 2014/rule/{entry.Slug} has no heading \"{entry.Heading}\".");
            }
            else
            {
                curatedHeadings.Add((entry.Slug, entry.Heading));
            }
        }

        foreach (var doc in documents.Where(d => d.Headings.Count > 0))
        {
            foreach (var heading in doc.Headings.Where(h => curatedHeadings.Contains((doc.Slug, h))))
            {
                Add(doc, heading, SrdIndexSchema.AliasFromHeading);
            }
        }

        var ordered = pairs.OrderBy(p => p.Doc2014.Id).ThenBy(p => p.Doc2024.Id).ToList();
        foreach (var pair in ordered.Where(p => p.Source == SrdIndexSchema.CounterpartBySection))
        {
            var key = SrdNames.Key(pair.Doc2024.Name);
            if (key.Length > 0 && $" {pair.Doc2014.TextKey} ".Contains($" {key} ", StringComparison.Ordinal))
            {
                Add(pair.Doc2014, pair.Doc2024.Name, SrdIndexSchema.AliasFromSection);
            }
        }

        foreach (var entry in SrdCuratedAliases.Names)
        {
            if (byRef.TryGetValue((entry.Edition, entry.Kind, entry.Slug), out var doc))
            {
                Add(doc, entry.Alias, SrdIndexSchema.AliasFromCurated);
            }
            else
            {
                warnings.Add($"Curated alias {entry} skipped: the content has no {entry.Edition}/{entry.Kind}/{entry.Slug}.");
            }
        }

        // Each side's names in the order the other side's records are gained (level, then content order).
        var renames = ordered.Where(p => p.Source != SrdIndexSchema.CounterpartBySection && p.Doc2014.Kind != SrdKinds.Level).ToList();
        foreach (var pair in renames.OrderBy(p => p.Doc2024.Level ?? int.MaxValue).ThenBy(p => p.Doc2024.Id))
        {
            Add(pair.Doc2014, pair.Doc2024.Name, SrdIndexSchema.AliasFromCounterpart);
        }

        foreach (var pair in renames.OrderBy(p => p.Doc2014.Level ?? int.MaxValue).ThenBy(p => p.Doc2014.Id))
        {
            Add(pair.Doc2024, pair.Doc2014.Name, SrdIndexSchema.AliasFromCounterpart);
        }

        return aliases;
    }

    private static void WriteAliases(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Dictionary<long, List<(string Alias, string Source)>> aliases)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO alias (doc_id, alias, alias_key, source, position) VALUES ($doc, $alias, $key, $source, $position);";
        var doc = insert.Parameters.Add(new SqliteParameter { ParameterName = "$doc" });
        var alias = insert.Parameters.Add(new SqliteParameter { ParameterName = "$alias" });
        var key = insert.Parameters.Add(new SqliteParameter { ParameterName = "$key" });
        var source = insert.Parameters.Add(new SqliteParameter { ParameterName = "$source" });
        var position = insert.Parameters.Add(new SqliteParameter { ParameterName = "$position" });

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE doc SET aliases = $aliases WHERE id = $doc;";
        var updateDoc = update.Parameters.Add(new SqliteParameter { ParameterName = "$doc" });
        var updateAliases = update.Parameters.Add(new SqliteParameter { ParameterName = "$aliases" });

        foreach (var (docId, list) in aliases.OrderBy(a => a.Key))
        {
            for (var i = 0; i < list.Count; i++)
            {
                doc.Value = docId;
                alias.Value = list[i].Alias;
                key.Value = SrdNames.Key(list[i].Alias);
                source.Value = list[i].Source;
                position.Value = i;
                insert.ExecuteNonQuery();
            }

            updateDoc.Value = docId;
            updateAliases.Value = string.Join('\n', list.Select(a => a.Alias));
            update.ExecuteNonQuery();
        }
    }

    private static void WriteCounterparts(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<CounterpartPair> pairs)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO counterpart (doc_2014, doc_2024, source) VALUES ($a, $b, $source);";
        var a = insert.Parameters.Add(new SqliteParameter { ParameterName = "$a" });
        var b = insert.Parameters.Add(new SqliteParameter { ParameterName = "$b" });
        var source = insert.Parameters.Add(new SqliteParameter { ParameterName = "$source" });
        foreach (var pair in pairs)
        {
            a.Value = pair.Doc2014.Id;
            b.Value = pair.Doc2024.Id;
            source.Value = pair.Source;
            insert.ExecuteNonQuery();
        }
    }

    // The manifest path of one edition's file. Reading only paths the manifest lists means the builder reads exactly
    // the files the verifier just checked, whatever the version directory is called.
    private static string ManifestPath(SrdIndexContent content, string edition, string fileName)
    {
        var suffix = $"/{edition}/{fileName}";
        var matches = content.Manifest.Files.Where(f => f.Path.EndsWith(suffix, StringComparison.Ordinal)).ToList();
        return matches.Count switch
        {
            1 => matches[0].Path,
            0 => throw new SrdIndexUnavailableException(
                $"The content manifest lists no {edition}/{fileName}. Re-vendor with scripts/fetch-5e-database.sh."),
            _ => throw new SrdIndexUnavailableException(
                $"The content manifest lists {edition}/{fileName} more than once ({string.Join(", ", matches.Select(m => m.Path))}). " +
                "Re-vendor with scripts/fetch-5e-database.sh."),
        };
    }

    private static IEnumerable<SourceRecord> ReadVendoredFile(
        SrdIndexContent content, string relativePath, string edition, string kind, List<string> warnings)
    {
        var fullPath = Path.Combine(content.DatabaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        using var document = ParseArray(File.ReadAllBytes(fullPath), relativePath);
        var position = 0;
        foreach (var vendored in document.RootElement.EnumerateArray())
        {
            var where = $"{relativePath} at $[{position}]";
            if (vendored.ValueKind != JsonValueKind.Object)
            {
                throw Unusable(where, $"is JSON {vendored.ValueKind}, not an object");
            }

            var slug = RequiredString(vendored, "index", where);
            var (element, reason) = Correct(content.Corrections, new SrdRef(edition, kind, slug), vendored, warnings);
            RequiredString(element, "url", where);

            string? classSlug = null, subclassSlug = null;
            int? level = null;
            string name;
            if (kind == SrdKinds.Level)
            {
                // 2014 level records have no name: "Fighter 5" / "Champion 3", as the formatters' vendored lookup does.
                var classObject = RequiredObject(element, "class", where);
                classSlug = RequiredString(classObject, "index", where + ".class");
                var hasSubclass = element.TryGetProperty("subclass", out _);
                var owner = classObject;
                var ownerWhere = where + ".class";
                if (hasSubclass)
                {
                    owner = RequiredObject(element, "subclass", where);
                    ownerWhere = where + ".subclass";
                    subclassSlug = RequiredString(owner, "index", ownerWhere);
                }

                if (!element.TryGetProperty("level", out var levelElement) || levelElement.ValueKind != JsonValueKind.Number ||
                    !levelElement.TryGetInt32(out var levelValue))
                {
                    throw Unusable(where, "has no integer \"level\"");
                }

                level = levelValue;
                name = OptionalName(element) ??
                       RequiredString(owner, "name", ownerWhere) + " " + levelValue.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                name = OptionalName(element) ?? throw Unusable(where, "has no string \"name\"");
                if (kind == SrdKinds.Feature)
                {
                    level = FeatureLevel(element);
                }
            }

            yield return new SourceRecord(relativePath, position, edition, kind, slug, name, element, classSlug, subclassSlug, level, reason);
            position++;
        }
    }

    private static IEnumerable<SourceRecord> ReadGlossary(SrdIndexContent content, List<string> warnings)
    {
        const string fileName = SrdKinds.RulesGlossary2024FileName;
        using var document = ParseArray(content.GlossaryBytes.ToArray(), fileName);
        var position = 0;
        foreach (var vendored in document.RootElement.EnumerateArray())
        {
            var where = $"{fileName} at $[{position}]";
            if (vendored.ValueKind != JsonValueKind.Object)
            {
                throw Unusable(where, $"is JSON {vendored.ValueKind}, not an object");
            }

            // The slug comes from the glossary's own name: it is the entry's identity, which a correction is keyed by.
            var vendoredName = OptionalName(vendored) ?? throw Unusable(where, "has no string \"name\"");
            var slug = SrdSlug.FromName(vendoredName);
            if (slug.Length == 0)
            {
                throw Unusable(where, $"has the name \"{vendoredName}\", which has no letters or digits to make a slug from");
            }

            var (element, reason) = Correct(content.Corrections, new SrdRef(SrdEdition.Edition2024, SrdKinds.Rule, slug), vendored, warnings);
            var name = OptionalName(element) ?? throw Unusable(where, "has no string \"name\" once corrected");
            yield return new SourceRecord(fileName, position, SrdEdition.Edition2024, SrdKinds.Rule, slug, name, element, null, null, null, reason);
            position++;
        }
    }

    // The record as the index sees it: the vendored element, or its correction and the reason. A correction that does
    // not fit the record (it sets a property the record lacks) is reported and the record kept as upstream wrote it.
    private static (JsonElement Element, string? Reason) Correct(
        SrdCorrections corrections, SrdRef reference, JsonElement vendored, List<string> warnings)
    {
        string? json;
        try
        {
            json = corrections.Apply(reference, vendored);
        }
        catch (InvalidDataException ex)
        {
            warnings.Add($"Correction for {reference} skipped: {ex.Message}");
            return (vendored, null);
        }

        if (json is null)
        {
            return (vendored, null);
        }

        using var corrected = JsonDocument.Parse(json);
        return (corrected.RootElement.Clone(), corrections.For(reference)!.Reason);
    }

    // The class level a feature is gained at: 2014 records have an integer "level"; 2024 records a reference to the
    // level record ({"index": "barbarian-4", "url": "/api/2024/classes/barbarian/levels/4"}). Null when neither says.
    private static int? FeatureLevel(JsonElement element)
    {
        if (!element.TryGetProperty("level", out var level))
        {
            return null;
        }

        if (level.ValueKind == JsonValueKind.Number)
        {
            return level.TryGetInt32(out var number) ? number : null;
        }

        if (level.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in new[] { "url", "index" })
            {
                if (level.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String &&
                    TrailingLevel().Match(text.GetString()!) is { Success: true } match &&
                    int.TryParse(match.Groups["level"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    return number;
                }
            }
        }

        return null;
    }

    // The slugs a 2014 magic item lists as its variants (Belt of Giant Strength → belt-of-giant-strength-hill, …).
    private static IReadOnlyList<string> VariantSlugs(JsonElement element) =>
        element.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array
            ? variants.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.String)
                .Select(v => v.GetProperty("index").GetString()!)
                .ToList()
            : [];

    // A 2014 rule's "####"/"#####" subsection headings, in text order, without markdown emphasis. Shallower headings are
    // separate rule records of their own; the tree puts only these inside a record's text.
    private static IReadOnlyList<string> SubsectionHeadings(JsonElement element)
    {
        if (!element.TryGetProperty("desc", out var desc))
        {
            return [];
        }

        var text = desc.ValueKind switch
        {
            JsonValueKind.String => desc.GetString()!,
            JsonValueKind.Array => string.Join('\n', desc.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString())),
            _ => string.Empty,
        };

        return text.ReplaceLineEndings("\n").Split('\n')
            .Select(line => SubsectionHeading().Match(line))
            .Where(m => m.Success)
            .Select(m => m.Groups["title"].Value.Replace("*", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).Trim())
            .Where(title => title.Length > 0)
            .ToList();
    }

    private static JsonDocument ParseArray(byte[] bytes, string file)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new SrdIndexUnavailableException($"{file} is not valid JSON: {ex.Message}", ex);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            throw new SrdIndexUnavailableException($"{file} is a JSON {document.RootElement.ValueKind}, not an array of records.");
        }

        return document;
    }

    private static string? OptionalName(JsonElement element) =>
        element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(name.GetString())
            ? name.GetString()
            : null;

    private static string RequiredString(JsonElement element, string property, string where)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw Unusable(where, $"has no string \"{property}\"");
    }

    private static JsonElement RequiredObject(JsonElement element, string property, string where) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw Unusable(where, $"has no \"{property}\" object");

    private static string? ClassIndexOf(JsonElement element) =>
        element.TryGetProperty("class", out var owner) && owner.ValueKind == JsonValueKind.Object &&
        owner.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.String
            ? index.GetString()
            : null;

    private static SrdIndexUnavailableException Unusable(string where, string problem) =>
        new($"{where} {problem}, so it cannot be indexed. Re-vendor with scripts/fetch-5e-database.sh " +
            "(or restore content/rules-glossary-2024.json) instead of editing records by hand.");

    // The record as stored: same values as the vendored bytes, without their indentation.
    private static string Compact(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, CompactJson))
        {
            element.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>How old another build's temp file must be before it counts as abandoned. A build takes about half a second.</summary>
    internal static readonly TimeSpan AbandonedTempFileAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Deletes temp databases a killed build left behind (<c>srd.db.&lt;pid&gt;.&lt;guid&gt;.tmp</c>), each with its journal.
    /// A failed build cleans up after itself, but a process killed mid-build cannot, and every leftover is a 13 MB file
    /// in the user's cache that nothing else would ever remove.
    ///
    /// <para>
    /// Only a temp database older than <see cref="AbandonedTempFileAge"/> goes: a younger one may belong to another
    /// server building right now. And a journal is never judged on its own. A live build's journal (journal_mode
    /// DELETE) is created and deleted with every statement, so a listed journal may be gone a moment later, and
    /// <see cref="File.GetLastWriteTimeUtc(string)"/> of a missing file is 1601-01-01, "very old"; deleting by that age
    /// removed the next statement's live journal and failed that build with SQLite's disk I/O error. A temp database
    /// is read once through <see cref="FileInfo"/>, skipped unless it exists, and only then takes its journal with it.
    /// </para>
    /// </summary>
    private static void DeleteAbandonedTempFiles(string directory, string targetFileName)
    {
        var cutoff = DateTime.UtcNow - AbandonedTempFileAge;
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(directory, targetFileName + ".*.tmp").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var path in candidates)
        {
            var temp = new FileInfo(path);
            if (temp.Name.EndsWith(".tmp", StringComparison.Ordinal) && temp.Exists && temp.LastWriteTimeUtc < cutoff)
            {
                DeleteQuietly(path);
                DeleteQuietly(path + "-journal");
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a failed build's own failure is the one worth reporting, and an abandoned file that cannot
            // be deleted now is tried again by the next build.
        }
    }

    // ".../levels/4" or "barbarian-4".
    [GeneratedRegex(@"[/-](?<level>\d{1,2})$")]
    private static partial Regex TrailingLevel();

    // "#### Grappling", "##### Variant: Encumbrance".
    [GeneratedRegex(@"^\s*#{4,5}\s+(?<title>.+?)\s*#*\s*$")]
    private static partial Regex SubsectionHeading();

    private sealed record SourceRecord(
        string File,
        int Position,
        string Edition,
        string Kind,
        string Slug,
        string Name,
        JsonElement Element,
        string? ClassSlug,
        string? SubclassSlug,
        int? Level,
        string? CorrectionReason);
}

/// <summary>
/// What one build produced. <see cref="Warnings"/> lists what the build skipped or ignored but did not refuse over:
/// manual counterparts, curated aliases and corrections the content no longer has, and files the content manifest
/// does not list.
/// </summary>
public sealed record SrdIndexBuildSummary(
    string StalenessKey,
    int DocumentCount,
    IReadOnlyList<SrdKindCount> Counts,
    IReadOnlyList<string> Warnings,
    DateTimeOffset BuiltAtUtc,
    TimeSpan Elapsed);

/// <summary>How many documents of one kind one edition has.</summary>
public sealed record SrdKindCount(string Edition, string Kind, int Count);
