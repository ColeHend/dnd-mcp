using System.Security.Cryptography;
using System.Text;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// The bump discipline behind the staleness key. srd.db is rebuilt only when its key changes, and the key sees the
/// content and <see cref="SrdIndexSchema.Version"/>, never the code, so an importer change shipped without a version
/// bump leaves every installed srd.db answering with the old tables. Tests build fresh indexes and cannot notice.
///
/// <para>
/// This pins a digest of the hand-written parts of the build (the DDL, the manual pair tables and the curated alias
/// lists) to the version: edit any of them and this fails until <see cref="SrdIndexSchema.Version"/> is bumped and the
/// pin below updated with it. Rule
/// changes in code are not covered (a digest of behaviour would move with every re-vendor and every correction); their
/// reminder is the per-source totals in <see cref="SrdCounterpartTests"/>, which such a change always moves.
/// </para>
/// </summary>
public sealed class SrdIndexSchemaVersionTests
{
    // Update BOTH together, and only after bumping SrdIndexSchema.Version.
    private const int PinnedVersion = 3;
    private const string PinnedDigest = "62f595f6a6047e16f86e7d8597f58c427ca7c5f8f655ce648be0fcd87628e2a8";

    [Fact]
    public void HandWrittenTables_Digest_IsPinnedToTheSchemaVersion()
    {
        // "\n" throughout, never AppendLine: the digest must be the same on Windows.
        var canonical = new StringBuilder()
            .Append(SrdIndexSchema.CreateTables.ReplaceLineEndings("\n")).Append('\n')
            .AppendJoin('\n', SrdCounterparts.Manual.Select(m => m.ToString())).Append('\n')
            .AppendJoin('\n', SrdCounterparts.Sections.Select(m => m.ToString())).Append('\n')
            .AppendJoin('\n', SrdCuratedAliases.Headings.Select(h => h.ToString())).Append('\n')
            .AppendJoin('\n', SrdCuratedAliases.Names.Select(n => n.ToString())).Append('\n')
            .ToString();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        Assert.True(
            (PinnedVersion, PinnedDigest) == (SrdIndexSchema.Version, digest),
            $"The srd.db tables, the manual pair lists or the curated alias lists changed (digest {digest}). Bump SrdIndexSchema.Version so " +
            "installed indexes rebuild, then pin the new version and this digest here.");
    }
}
