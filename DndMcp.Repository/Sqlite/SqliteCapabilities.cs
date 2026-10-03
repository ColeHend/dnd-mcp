using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Sqlite;

/// <summary>
/// What the native SQLite library loaded into this process can actually do, measured by exercising each
/// feature rather than assumed from the package version.
///
/// <para>
/// campaigns.db and srd.db are built on FTS5, the JSON functions, STRICT tables, partial and expression
/// indexes, RAISE(ABORT) triggers, WAL and VACUUM INTO (PLAN.md D2 and "6. Campaign tracking"). Every one of
/// those belongs to the NATIVE library, not to Microsoft.Data.Sqlite: the e_sqlite3 bundled with
/// Microsoft.Data.Sqlite has them all, but a changed SQLitePCLRaw bundle, a system libsqlite3 loaded instead,
/// or an old distro build can lack any of them. Without this probe the first symptom is a migration dying
/// half-way through with "no such module: fts5" or a syntax error at STRICT — or, for WAL, nothing at all,
/// because <c>PRAGMA journal_mode=WAL</c> silently keeps the old mode when WAL is unavailable.
/// </para>
/// <para>
/// Each flag is set by using the feature and checking the result (a stemmed and accent-folded match, a
/// rejected wrong-typed insert, a planner that picks the index), so a build that parses the syntax but
/// behaves differently still reports the feature missing. One exception: <see cref="Fts5PrefixIndexes"/>
/// shows that <c>prefix = '2 3'</c> is accepted and prefix queries return the right rows. A prefix index only
/// changes speed, so no result can prove it was built. The WAL flag describes the library only: the
/// campaign DB opener must still check that <c>PRAGMA journal_mode=WAL</c> returns "wal" on the real file,
/// because WAL also depends on the file system under it (network file systems cannot share its memory map).
/// </para>
/// <para>
/// <b>Who calls it.</b> The host, once at startup: DndMcp's <c>SqliteCapabilityCheck</c> (the first hosted service, so it
/// runs before the rules index warms up and before <c>initialize</c> is answered) probes in the data directory, logs this
/// record to stderr and calls <see cref="EnsureSupported"/>, so a bad library fails the process once, with the full list,
/// before any tool touches a database. WAL is judged on campaigns.db's own file instead (the migrator warns and carries
/// on in rollback-journal mode), so both callers check every other flag with <see cref="WalJournalMode"/> forced true.
/// The other caller is <c>CampaignDatabase</c>, once per process before it first opens campaigns.db: the in-memory test
/// host runs no hosted services, and the startup probe only warns when the data directory cannot be written. Dropping
/// either call means the first symptom of a changed library is a half-applied migration again.
/// </para>
/// </summary>
public sealed record SqliteCapabilities(
    string Version,
    string NativeLibrary,
    bool MeetsMinimumVersion,
    bool Fts5,
    bool Fts5PorterUnicode61Tokenizer,
    bool Fts5PrefixIndexes,
    bool Fts5Bm25,
    bool JsonFunctions,
    bool StrictTables,
    bool WithoutRowidTables,
    bool PartialIndexes,
    bool JsonExpressionIndexes,
    bool TriggerRaiseAbort,
    bool WalJournalMode,
    bool VacuumInto)
{
    /// <summary>
    /// STRICT tables arrived in 3.37.0 and are the newest feature the schema needs, so this is the floor.
    /// Every other required feature is older (VACUUM INTO and remove_diacritics 2 are 3.27, expression
    /// indexes 3.9). Raising the floor is a deliberate schema decision, not a dependency bump.
    /// </summary>
    public const string MinimumVersion = "3.37.0";

    /// <summary>The name of every probe's scratch directory, before its 32 hex digits.</summary>
    internal const string ScratchPrefix = "dnd-mcp-sqlite-probe-";

    /// <summary>
    /// How old (by its last write) another probe's scratch directory must be before a probe deletes it: a probe takes
    /// milliseconds, so one that old was left by a process killed mid-probe, while a younger one may be another server's
    /// probe running right now (two Claude sessions often start together).
    /// </summary>
    internal static readonly TimeSpan StaleScratchAge = TimeSpan.FromMinutes(1);

    private const int SqliteConstraint = 19;

    /// <summary>
    /// Probes in a scratch directory under the system temp path. A file-backed database is required: WAL
    /// cannot be enabled on <c>:memory:</c> (the pragma answers "memory"), and VACUUM INTO needs somewhere to
    /// write. The scratch directory is deleted afterwards so a server that starts once per Claude session does
    /// not accumulate litter.
    /// </summary>
    public static SqliteCapabilities Probe() => Probe(Path.GetTempPath());

    /// <summary>
    /// Probes in a fresh subdirectory of <paramref name="scratchParentDirectory"/> and deletes it afterwards.
    /// The probe never opens campaigns.db or srd.db: checking WAL there would change the real file's journal
    /// mode, and a probe must not mutate what it measures.
    ///
    /// <para>
    /// First it deletes the scratch directories earlier probes left there (<see cref="StaleScratchAge"/> or older).
    /// The finally below cannot run when a process is killed mid-probe (SIGKILL: Claude Code closing a session that is
    /// just starting), and the host probes in the data directory, beside campaigns.db, so without this an empty
    /// <c>dnd-mcp-sqlite-probe-*</c> directory would now and then be left there for good. Only directories named exactly
    /// like a probe's are touched.
    /// </para>
    /// </summary>
    public static SqliteCapabilities Probe(string scratchParentDirectory)
    {
        DeleteStaleScratch(scratchParentDirectory);
        var scratch = Path.Combine(scratchParentDirectory, ScratchPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            // Pooling=False: a pooled connection keeps the file (and its -wal/-shm) open after Dispose, which
            // would stop the scratch directory from being deleted on Windows.
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(scratch, "probe.db"),
                Pooling = false,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var version = connection.ServerVersion;
            var wal = Check(connection, ProbeWal);
            var fts5 = Check(connection, ProbeFts5);
            var tokenizer = Check(connection, ProbeTokenizer);
            var prefix = Check(connection, ProbePrefixIndexes);
            var bm25 = Check(connection, ProbeBm25);
            var json = Check(connection, ProbeJsonFunctions);
            var strict = Check(connection, ProbeStrictTables);
            var withoutRowid = Check(connection, ProbeWithoutRowid);
            var partial = Check(connection, ProbePartialIndexes);
            var expression = Check(connection, ProbeJsonExpressionIndexes);
            var raiseAbort = Check(connection, ProbeTriggerRaiseAbort);
            var vacuumInto = Check(connection, c => ProbeVacuumInto(c, Path.Combine(scratch, "probe-copy.db")));

            return new SqliteCapabilities(
                Version: version,
                NativeLibrary: SQLitePCL.raw.GetNativeLibraryName(),
                MeetsMinimumVersion: IsAtLeastMinimum(version),
                Fts5: fts5,
                Fts5PorterUnicode61Tokenizer: tokenizer,
                Fts5PrefixIndexes: prefix,
                Fts5Bm25: bm25,
                JsonFunctions: json,
                StrictTables: strict,
                WithoutRowidTables: withoutRowid,
                PartialIndexes: partial,
                JsonExpressionIndexes: expression,
                TriggerRaiseAbort: raiseAbort,
                WalJournalMode: wal,
                VacuumInto: vacuumInto);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    /// <summary>
    /// Human-readable names of everything missing, in a stable order. Empty means the library is usable.
    /// Where it is not obvious, a name also says what depends on the feature, because the reader of this list
    /// is someone looking at a startup failure, not at this code.
    /// </summary>
    public IReadOnlyList<string> MissingFeatures()
    {
        var missing = new List<string>();
        if (!MeetsMinimumVersion)
        {
            missing.Add($"SQLite {MinimumVersion} or newer (found {Version})");
        }

        if (!Fts5)
        {
            missing.Add("FTS5 full-text search (rules_search, campaign_search)");
        }

        if (!Fts5PorterUnicode61Tokenizer)
        {
            missing.Add("FTS5 tokenizer 'porter unicode61 remove_diacritics 2' (stemming and accent folding)");
        }

        if (!Fts5PrefixIndexes)
        {
            missing.Add("FTS5 prefix indexes (prefix = '2 3')");
        }

        if (!Fts5Bm25)
        {
            missing.Add("FTS5 bm25() ranking with per-column weights");
        }

        if (!JsonFunctions)
        {
            missing.Add("JSON functions json_patch, json_extract and json_valid");
        }

        if (!StrictTables)
        {
            missing.Add("STRICT tables");
        }

        if (!WithoutRowidTables)
        {
            missing.Add("WITHOUT ROWID tables");
        }

        if (!PartialIndexes)
        {
            missing.Add("partial indexes");
        }

        if (!JsonExpressionIndexes)
        {
            missing.Add("expression indexes on json_extract");
        }

        if (!TriggerRaiseAbort)
        {
            missing.Add("triggers with RAISE(ABORT) (append-only change_log)");
        }

        if (!WalJournalMode)
        {
            missing.Add("WAL journal mode (several Claude sessions share campaigns.db)");
        }

        if (!VacuumInto)
        {
            missing.Add("VACUUM INTO (backups)");
        }

        return missing;
    }

    /// <summary>
    /// Throws when anything is missing, naming every gap at once plus the minimum version, so one restart
    /// cycle fixes the environment instead of one error per launch. InvalidOperationException, not
    /// DndInputException: nothing the model sends can fix a native library, so this must stop the host
    /// rather than become a tool error.
    /// </summary>
    public void EnsureSupported()
    {
        var missing = MissingFeatures();
        if (missing.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The SQLite library loaded by dnd-mcp ({NativeLibrary} {Version}) is missing: " +
            string.Join("; ", missing) + ". " +
            $"dnd-mcp needs SQLite {MinimumVersion} or newer built with FTS5 and the JSON functions. The " +
            "e_sqlite3 library bundled with Microsoft.Data.Sqlite has all of these, so this usually means a " +
            "different native SQLite was loaded (a system libsqlite3 or a changed SQLitePCLRaw bundle).");
    }

    private static bool IsAtLeastMinimum(string version) =>
        System.Version.TryParse(version, out var parsed) && parsed >= System.Version.Parse(MinimumVersion);

    /// <summary>
    /// Only SqliteException means "the library cannot do this". Anything else (I/O on the scratch
    /// directory, a bug in a probe) propagates, because reporting it as a missing feature would send someone
    /// hunting for the wrong problem.
    /// </summary>
    private static bool Check(SqliteConnection connection, Func<SqliteConnection, bool> probe)
    {
        try
        {
            return probe(connection);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static bool ProbeWal(SqliteConnection c) =>
        string.Equals(Scalar<string>(c, "PRAGMA journal_mode=WAL"), "wal", StringComparison.OrdinalIgnoreCase);

    private static bool ProbeFts5(SqliteConnection c)
    {
        Execute(c, "CREATE VIRTUAL TABLE probe_fts USING fts5(body)");
        Execute(c, "INSERT INTO probe_fts(body) VALUES ('alpha beta')");
        return Scalar<long>(c, "SELECT count(*) FROM probe_fts WHERE probe_fts MATCH 'beta'") == 1;
    }

    private static bool ProbeTokenizer(SqliteConnection c)
    {
        // One query needs every part: "dragon" only matches "Dragons" through porter stemming, and "nguyen"
        // only matches "Nguyễn" through remove_diacritics 2. The ễ carries two diacritics, which mode 1
        // (unicode61's default) leaves unfolded, so a library that ignored the "2" fails here.
        Execute(c, "CREATE VIRTUAL TABLE probe_fts_tokenizer USING fts5(body, tokenize = 'porter unicode61 remove_diacritics 2')");
        Execute(c, "INSERT INTO probe_fts_tokenizer(body) VALUES ('Nguyễn hunts Dragons')");
        return Scalar<long>(c, "SELECT count(*) FROM probe_fts_tokenizer WHERE probe_fts_tokenizer MATCH 'nguyen dragon'") == 1;
    }

    private static bool ProbePrefixIndexes(SqliteConnection c)
    {
        Execute(c, "CREATE VIRTUAL TABLE probe_fts_prefix USING fts5(body, prefix = '2 3')");
        Execute(c, "INSERT INTO probe_fts_prefix(body) VALUES ('Belmakor')");
        return Scalar<long>(c, "SELECT count(*) FROM probe_fts_prefix WHERE probe_fts_prefix MATCH 'be*'") == 1
            && Scalar<long>(c, "SELECT count(*) FROM probe_fts_prefix WHERE probe_fts_prefix MATCH 'bel*'") == 1;
    }

    private static bool ProbeBm25(SqliteConnection c)
    {
        Execute(c, "CREATE VIRTUAL TABLE probe_fts_rank USING fts5(name, body)");
        Execute(c, "INSERT INTO probe_fts_rank(name, body) VALUES ('dragon', 'a wyrm'), ('ship', 'no match here')");
        var score = Scalar<double>(c, "SELECT bm25(probe_fts_rank, 10.0, 1.0) FROM probe_fts_rank WHERE probe_fts_rank MATCH 'dragon'");
        return score < 0 && double.IsFinite(score);
    }

    private static bool ProbeJsonFunctions(SqliteConnection c) =>
        Scalar<long>(c, """
            SELECT json_patch('{"a":{"b":1,"c":2}}', '{"a":{"c":null,"d":3}}') = '{"a":{"b":1,"d":3}}'
               AND json_extract('{"x":{"y":7}}', '$.x.y') = 7
               AND json_valid('{"a":1}') = 1
               AND json_valid('{bad') = 0
            """) == 1;

    private static bool ProbeStrictTables(SqliteConnection c)
    {
        Execute(c, "CREATE TABLE probe_strict(n INTEGER NOT NULL) STRICT");
        return Rejects(c, "INSERT INTO probe_strict(n) VALUES ('not a number')");
    }

    private static bool ProbeWithoutRowid(SqliteConnection c)
    {
        Execute(c, "CREATE TABLE probe_without_rowid(k TEXT PRIMARY KEY, v TEXT NOT NULL) WITHOUT ROWID");
        Execute(c, "INSERT INTO probe_without_rowid(k, v) VALUES ('a', 'b')");
        return Scalar<string>(c, "SELECT v FROM probe_without_rowid WHERE k = 'a'") == "b";
    }

    private static bool ProbePartialIndexes(SqliteConnection c)
    {
        Execute(c, "CREATE TABLE probe_partial(kind TEXT NOT NULL, v INTEGER)");
        Execute(c, "CREATE INDEX probe_ix_partial ON probe_partial(v) WHERE kind = 'character'");
        return QueryPlan(c, "SELECT v FROM probe_partial WHERE kind = 'character' AND v = 1").Contains("probe_ix_partial");
    }

    private static bool ProbeJsonExpressionIndexes(SqliteConnection c)
    {
        Execute(c, "CREATE TABLE probe_expression(data TEXT NOT NULL)");
        Execute(c, "CREATE INDEX probe_ix_expression ON probe_expression(json_extract(data, '$.attitude'))");
        var plan = QueryPlan(c, "SELECT data FROM probe_expression WHERE json_extract(data, '$.attitude') = 1");
        return plan.Contains("probe_ix_expression") && plan.Contains("<expr>");
    }

    private static bool ProbeTriggerRaiseAbort(SqliteConnection c)
    {
        Execute(c, "CREATE TABLE probe_append_only(v TEXT NOT NULL)");
        Execute(c, "CREATE TRIGGER probe_append_only_no_delete BEFORE DELETE ON probe_append_only BEGIN SELECT RAISE(ABORT, 'append-only'); END");
        Execute(c, "INSERT INTO probe_append_only(v) VALUES ('kept')");
        return Rejects(c, "DELETE FROM probe_append_only")
            && Scalar<long>(c, "SELECT count(*) FROM probe_append_only") == 1;
    }

    private static bool ProbeVacuumInto(SqliteConnection c, string copyPath)
    {
        Execute(c, "CREATE TABLE probe_vacuum(v TEXT NOT NULL)");
        Execute(c, "INSERT INTO probe_vacuum(v) VALUES ('copied')");
        using (var command = c.CreateCommand())
        {
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", copyPath);
            command.ExecuteNonQuery();
        }

        var copyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = copyPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var copy = new SqliteConnection(copyConnectionString);
        copy.Open();
        return Scalar<string>(copy, "SELECT v FROM probe_vacuum") == "copied";
    }

    private static bool Rejects(SqliteConnection c, string sql)
    {
        try
        {
            Execute(c, sql);
            return false;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint)
        {
            return true;
        }
    }

    private static string QueryPlan(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
        {
            // Columns are id, parent, notused, detail.
            details.Add(reader.GetString(3));
        }

        return string.Join(" / ", details);
    }

    private static void Execute(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// No row or NULL becomes <c>default</c>, which every probe reads as "feature missing". Converting a NULL
    /// would throw InvalidCastException instead, and that would escape <see cref="Check"/> and take the host
    /// down over a feature that is merely absent.
    /// </summary>
    private static T? Scalar<T>(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? default
            : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Cleanup failure is swallowed on purpose (I/O only): a leftover few-KB directory in temp is harmless,
    /// while throwing here would stop the host from starting over litter.
    /// </summary>
    private static void DeleteScratch(string scratch)
    {
        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Deletes earlier probes' scratch directories (<see cref="Probe(string)"/>'s summary). Their age is their last
    /// write against the system clock, which is what stamped it. A parent that does not exist or cannot be listed has
    /// nothing to clean, and a directory that cannot be deleted is left: as in <see cref="DeleteScratch"/>, litter must
    /// never stop a start.
    /// </summary>
    private static void DeleteStaleScratch(string scratchParentDirectory)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(scratchParentDirectory, ScratchPrefix + "*").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var staleBefore = DateTime.UtcNow - StaleScratchAge;
        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (name.Length != ScratchPrefix.Length + 32 || !name[ScratchPrefix.Length..].All(char.IsAsciiHexDigitLower))
            {
                continue;
            }

            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < staleBefore)
                {
                    DeleteScratch(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
