using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// A campaigns.db of its own for one test: a fresh temp directory, a <see cref="CampaignDatabase"/> on
/// <c>&lt;dir&gt;/campaigns.db</c> with a <see cref="ManualTimeProvider"/> and a <see cref="ListLogger{T}"/>, migrated on
/// construction (unless <c>create: false</c>), and the whole directory (backups included) deleted on Dispose.
///
/// <para>
/// A file, never <c>:memory:</c>: WAL, VACUUM INTO backups, two connections at once and restore only exist on files.
/// Deleting the directory works because every campaign connection is <c>Pooling=False</c> (a pooled handle would keep the
/// file and its -wal open after Dispose).
/// </para>
/// <para>
/// Used by later stages: seed with <see cref="CampaignSeed"/> (raw SQL, no change_log) through <see cref="Open"/>, or
/// write logged batches with <see cref="Batch(string, Action{ChangeRecorder}, string?, string?, string?)"/>.
/// </para>
/// </summary>
public sealed class CampaignTestDb : IDisposable
{
    /// <param name="time">The clock (default: a new <see cref="ManualTimeProvider"/> at its default start).</param>
    /// <param name="create">False leaves the directory empty and the database unopened.</param>
    public CampaignTestDb(ManualTimeProvider? time = null, bool create = true)
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "dnd-mcp-campaign-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        Time = time ?? new ManualTimeProvider();
        Logger = new ListLogger<CampaignDatabase>();
        Database = new CampaignDatabase(DatabasePath, Time, Logger);
        if (create)
        {
            Database.EnsureReady();
        }
    }

    public string DirectoryPath { get; }

    public string DatabasePath => Path.Combine(DirectoryPath, "campaigns.db");

    public string BackupsPath => Path.Combine(DirectoryPath, CampaignBackups.DirectoryName);

    public ManualTimeProvider Time { get; }

    public ListLogger<CampaignDatabase> Logger { get; }

    public CampaignDatabase Database { get; }

    /// <summary>A connection with the house settings (the caller disposes it).</summary>
    public SqliteConnection Open() => Database.OpenRead();

    /// <summary>A second <see cref="CampaignDatabase"/> on the same file: what another server process would hold.</summary>
    public CampaignDatabase OtherProcess(ListLogger<CampaignDatabase>? logger = null) =>
        new(DatabasePath, Time, logger ?? new ListLogger<CampaignDatabase>());

    /// <summary>
    /// Runs one logged batch through <see cref="CampaignDatabase.Write{T}"/> with a <see cref="ChangeRecorder"/> for
    /// <paramref name="campaignId"/>; returns the batch id.
    /// </summary>
    public string Batch(string campaignId, Action<ChangeRecorder> work, string? sessionId = null, string? reason = null, string? batchId = null)
    {
        var context = new BatchContext(campaignId, batchId ?? CampaignDatabase.NewId(), CampaignValues.Actors.Claude,
            "test", sessionId, reason);
        return Database.Write((connection, transaction) =>
        {
            work(new ChangeRecorder(connection, transaction, context, Database.Now()));
            return context.BatchId;
        });
    }

    /// <summary>Undoes a batch as a new batch; returns the result.</summary>
    public UndoResult Undo(string campaignId, string batchIdOrPrefix, string? sessionId = null, string? reason = null) =>
        Database.Write((connection, transaction) => UndoEngine.Undo(connection, transaction, campaignId, batchIdOrPrefix,
            BatchContext.New(campaignId, CampaignValues.Actors.Claude, "campaign_history/undo", sessionId, reason), Database.Now()));

    /// <summary>
    /// Damages campaigns.db the way a bad sector or a torn copy does: <paramref name="table"/>'s root page is overwritten
    /// with noise past its first 8 header bytes, after the -wal is checkpointed into the file. The file header and schema
    /// stay intact, so the file opens and migrates as usual; the first read of that table fails with SQLITE_CORRUPT.
    /// </summary>
    public void ScrambleRootPage(string table)
    {
        long root;
        long pageSize;
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            connection.ExecuteScalar<string>("PRAGMA wal_checkpoint(TRUNCATE)");
            root = connection.ExecuteScalar<long>("SELECT rootpage FROM sqlite_master WHERE type = 'table' AND name = @table", new { table });
            pageSize = connection.ExecuteScalar<long>("PRAGMA page_size");
        }

        var noise = new byte[pageSize - 8];
        new Random(7).NextBytes(noise);
        using var file = new FileStream(DatabasePath, FileMode.Open, FileAccess.Write);
        file.Seek(((root - 1) * pageSize) + 8, SeekOrigin.Begin);
        file.Write(noise);
    }

    public void Dispose()
    {
        Database.Dispose();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }
}
