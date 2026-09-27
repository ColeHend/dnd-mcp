using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using DndMcp.Hosting;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using ModelContextProtocol.Server;

namespace DndMcp.Resources;

/// <summary>
/// <c>rules://attribution</c>: the licence statements for the SRD text the rules tools return, and where that text comes
/// from.
///
/// <para>
/// The SRD 5.1 and SRD 5.2.1 are CC-BY-4.0, which requires their attribution statements to accompany the material, and
/// both PDFs ask for exactly these statements and no other credit to Wizards of the Coast. The statements are constants
/// here, not read from content/, so the obligation is met even by an install whose content directory is broken; the tests
/// pin them to the shipped licence files and the README, so the three copies cannot drift apart. What depends on the
/// content (the 5e-database tag, the glossary's hash, the curated corrections, the MIT notice that ships with the data) is
/// read from content/ on each request and reported as missing, rather than failing the resource, when it cannot be read.
/// </para>
/// <para>
/// The curated corrections are listed because CC-BY 4.0 asks that modifications be indicated: hundreds of served records
/// carry replacement text copied from the SRD markdown instead of the upstream data. Each corrected entry says so under its
/// title; this is the provenance record for all of them. <c>rules_get</c> also answers with this text
/// (<c>ref "rules://attribution"</c>), because Claude Desktop attaches resources only by hand.
/// </para>
/// </summary>
public sealed class RulesResources
{
    public const string AttributionUri = "rules://attribution";

    /// <summary>The SRD 5.1 statement, verbatim (content/LICENSES/SRD-5.1-CC-BY-4.0.txt and the README).</summary>
    public const string Srd51Statement =
        "This work includes material taken from the System Reference Document 5.1 (\"SRD 5.1\") by Wizards of the Coast LLC " +
        "and available at https://dnd.wizards.com/resources/systems-reference-document. The SRD 5.1 is licensed under the " +
        "Creative Commons Attribution 4.0 International License available at " +
        "https://creativecommons.org/licenses/by/4.0/legalcode.";

    /// <summary>The SRD 5.2.1 statement, verbatim (content/LICENSES/SRD-5.2.1-CC-BY-4.0.txt and the README).</summary>
    public const string Srd521Statement =
        "This work includes material from the System Reference Document 5.2.1 (\"SRD 5.2.1\") by Wizards of the Coast LLC, " +
        "available at https://www.dndbeyond.com/srd. The SRD 5.2.1 is licensed under the Creative Commons Attribution 4.0 " +
        "International License, available at https://creativecommons.org/licenses/by/4.0/legalcode.";

    /// <summary>
    /// The served tables that are not from either SRD. The CC-BY statements above cover only SRD text, and a reader of this
    /// page must not take the 2014 DMG's encounter tables for it (PLAN.md, open question 1: included for personal use).
    /// </summary>
    public const string NotSrdText =
        "The 2014 encounter-building tables (`rules://tables/xp-thresholds-2014`, `rules://tables/encounter-multipliers-2014`, " +
        "`rules://tables/adventuring-day-xp-2014`) and the Monster Statistics by Challenge Rating table " +
        "(`rules://tables/monster-stats-by-cr-2014`) come from the Dungeon Master's Guide (2014), the first three also from " +
        "the free 2014 Basic Rules, not from SRD 5.1, so the licence above does not cover them. They are served for personal " +
        "use, and every result that uses them names that source.";

    /// <summary>The 5e-database licence, relative to the content root.</summary>
    public static readonly string MitNoticeRelativePath = Path.Combine("LICENSES", "5e-database-MIT.txt");

    private readonly DndMcpServerOptions _options;

    public RulesResources(DndMcpServerOptions options)
    {
        _options = options;
    }

    [McpServerResource(UriTemplate = AttributionUri, Name = "attribution", Title = "SRD attribution and licences", MimeType = "text/markdown")]
    [Description(
        "Licence statements for the SRD 5.1 (2014) and SRD 5.2.1 (2024) text that rules_search and rules_get return, the " +
        "5e-database MIT notice, the exact data versions served, the curated corrections applied to them, and which served " +
        "tables are not SRD text.")]
    public string Attribution()
    {
        var contentRoot = _options.ContentRoot;
        var text = new StringBuilder()
            .Append("# SRD attribution\n\n")
            .Append("Rules text from `rules_search` and `rules_get` comes from the two System Reference Documents below. ")
            .Append("Each result names its source.\n\n")
            .Append("## SRD 5.1 (2014 rules)\n\n").Append(Srd51Statement).Append("\n\n")
            .Append("## SRD 5.2.1 (2024 rules)\n\n").Append(Srd521Statement).Append("\n\n")
            .Append("## Data\n\n")
            .Append("- ").Append(DatabaseProvenance(contentRoot)).Append('\n')
            .Append("- ").Append(GlossaryProvenance(contentRoot)).Append('\n')
            .Append("- ").Append(CorrectionsProvenance(contentRoot)).Append("\n\n")
            .Append("## Not SRD text\n\n").Append(NotSrdText).Append("\n\n")
            .Append("## 5e-database licence (MIT)\n\n")
            .Append(MitNotice(contentRoot));

        return text.ToString().TrimEnd() + "\n";
    }

    private static string DatabaseProvenance(string contentRoot)
    {
        var manifestPath = Path.Combine(contentRoot, SrdIndexContent.DatabaseDirectoryName, ContentManifest.FileName);
        try
        {
            var manifest = ContentManifest.Load(manifestPath);
            return $"2014 and 2024 records: the 5e-bits 5e-database dataset at tag `{manifest.Tag}` ({manifest.Repository}, " +
                   $"`{manifest.SourcePath}`), {manifest.Files.Count} files pinned by sha256. MIT licence below.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"2014 and 2024 records: the 5e-bits 5e-database dataset (MIT licence below). Its manifest could not be read " +
                   $"from {manifestPath} ({ex.Message}), so the version served is unknown.";
        }
    }

    private static string GlossaryProvenance(string contentRoot)
    {
        var glossaryPath = Path.Combine(contentRoot, SrdKinds.RulesGlossary2024FileName);
        const string What = "2024 rules: the SRD 5.2.1 Rules Glossary as structured JSON, from the serving-solid-characters " +
                            "project (https://github.com/ColeHend/serving-solid-characters)";
        try
        {
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(glossaryPath)));
            return $"{What}, sha256 `{sha256}`.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{What}. The file could not be read from {glossaryPath} ({ex.Message}).";
        }
    }

    private static string CorrectionsProvenance(string contentRoot)
    {
        const string What = "Curated corrections: `content/" + SrdCorrections.FileName + "`";
        const string How =
            "whose damaged upstream text or data (spliced from another entry, run together, cut short, back-translated) was " +
            "replaced with the SRD's own words, copied from the SRD 5.2 markdown for 2024 and the SRD 5.1 markdown for 2014 " +
            "(both CC-BY-4.0). Every corrected entry says so under its title (\"Corrected from the upstream data: …\")";
        try
        {
            var corrections = SrdCorrections.Load(contentRoot);
            var perEdition = SrdEdition.All
                .Select(e => (Edition: e, Count: corrections.Entries.Count(c => c.Target.Edition == e)))
                .Where(e => e.Count > 0)
                .Select(e => $"{e.Count} in {e.Edition}");
            return $"{What}, {corrections.Entries.Count} records ({string.Join(", ", perEdition)}) {How}; sha256 " +
                   $"`{corrections.Sha256}`.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"{What}: records {How}. The file could not be read from {Path.Combine(contentRoot, SrdCorrections.FileName)} " +
                   $"({ex.Message}).";
        }
    }

    private static string MitNotice(string contentRoot)
    {
        var path = Path.Combine(contentRoot, MitNoticeRelativePath);
        try
        {
            return "```text\n" + File.ReadAllText(path).Trim() + "\n```";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The notice ships as content/{MitNoticeRelativePath.Replace('\\', '/')} but could not be read from {path} " +
                   $"({ex.Message}); it is also in the dnd-mcp repository.";
        }
    }
}
