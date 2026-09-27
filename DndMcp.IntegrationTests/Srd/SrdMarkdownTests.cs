using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: <see cref="SrdMarkdown.Compare"/> is shaped so the model can tell the editions apart: one title, the
/// glance table (spells and monsters only), then each edition's whole entry under its own <c>##</c> heading with any note
/// about that side under its meta line, and for a side nothing matched an explicit note that the lookup found nothing
/// (never that the SRD lacks it) rather than a silently one-sided answer. Notes passed as a trailer stay whole within the
/// cap.
///
/// <para>
/// The bodies' own headings are <c>###</c> or deeper, so the only <c>#</c>/<c>##</c> lines are the ones this shape
/// puts there; a body heading that outranked "## 2024" would read as the start of a third section.
/// </para>
/// </summary>
public sealed class SrdMarkdownTests
{
    private static VendoredSrdLookup Lookup => VendoredSrdLookup.Instance;

    [Fact]
    public void Compare_SpellInBothEditions_IsGlanceThenEachEditionUnderItsHeading()
    {
        var text = Compare("2014/spell/fireball", "2024/spell/fireball");

        Assert.StartsWith("# Fireball — 2014 vs 2024\n\n## At a glance\n\n| Field | 2014 | 2024 | Changed |\n", text, StringComparison.Ordinal);
        Assert.Equal(new[] { "# Fireball — 2014 vs 2024", "## At a glance", "## 2014 (SRD 5.1)", "## 2024 (SRD 5.2.1)" }, TopHeadings(text));

        var side2014 = Between(text, "## 2014 (SRD 5.1)", "## 2024 (SRD 5.2.1)");
        var side2024 = Between(text, "## 2024 (SRD 5.2.1)", null);
        Assert.Contains("*spell · 2014 · SRD 5.1 · `2014/spell/fireball`*", side2014, StringComparison.Ordinal);
        Assert.Contains("***At Higher Levels.***", side2014, StringComparison.Ordinal);
        Assert.Contains("**Damage** Fire (`2014/damage-type/fire`) — 8d6 (slot 3)", side2014, StringComparison.Ordinal);
        Assert.Contains("*spell · 2024 · SRD 5.2.1 · `2024/spell/fireball`*", side2024, StringComparison.Ordinal);
        Assert.Contains("***Using a Higher-Level Spell Slot.***", side2024, StringComparison.Ordinal);
        Assert.DoesNotContain("2014/", side2024, StringComparison.Ordinal);
        Assert.DoesNotContain("2024/", side2014, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_RenamedMonster_TitleNamesBothAndGlanceComparesThem()
    {
        var text = Compare("2014/monster/thug", "2024/monster/tough");

        Assert.StartsWith("# Thug (2014) / Tough (2024) — 2014 vs 2024\n\n## At a glance\n\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## 2014 (SRD 5.1)\n\n*monster · 2014 · SRD 5.1 · `2014/monster/thug`*", text, StringComparison.Ordinal);
        Assert.Contains("\n## 2024 (SRD 5.2.1)\n\n*monster · 2024 · SRD 5.2.1 · `2024/monster/tough`*", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2014/spell/feeblemind", null,
        "## 2024 (SRD 5.2.1)\n\n*No 2024 entry matched this one by a known rename or by name. The 2024 SRD may cover it under another " +
        "heading — try rules_search with edition 2024.*")]
    [InlineData(null, "2024/spell/sorcerous-burst",
        "## 2014 (SRD 5.1)\n\n*No 2014 entry matched this one by a known rename or by name. The 2014 SRD may cover it under another " +
        "heading — try rules_search with edition 2014.*")]
    [InlineData(null, "2024/monster/goblin-boss",
        "## 2014 (SRD 5.1)\n\n*No 2014 entry matched this one by a known rename or by name. The 2014 SRD may cover it under another " +
        "heading — try rules_search with edition 2014.*")]
    public void Compare_EntryInOneEditionOnly_SaysNothingMatchedAndHasNoGlance(string? reference2014, string? reference2024, string note)
    {
        // A missing side means the lookup found nothing, not that the SRD lacks the rule: 2014 keeps Grappling inside Melee
        // Attacks and Serpent Venom inside Sample Poisons. "No 2014 equivalent in the SRD" was relayed by models as a fact.
        var text = Compare(reference2014, reference2024);
        var present = Lookup.Require((reference2014 ?? reference2024)!);

        Assert.StartsWith($"# {present.Name} — 2014 vs 2024\n\n## 2014 (SRD 5.1)\n\n", text, StringComparison.Ordinal);
        Assert.Contains(note, text, StringComparison.Ordinal);
        Assert.Contains($"`{present.Ref}`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## At a glance", text, StringComparison.Ordinal);
        Assert.DoesNotContain("equivalent in the SRD", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_SideNotes_SitUnderEachSidesMetaLine()
    {
        // A note about one side (how it was matched) belongs with that side, where the model reads the entry it qualifies.
        var text = SrdMarkdown.Compare(
            Lookup.Require("2014/spell/fireball"),
            Lookup.Require("2024/spell/fireball"),
            SrdMarkdown.Concise,
            Lookup,
            note2014: "*Note on 2014.*",
            note2024: "*Note on 2024.*");

        Assert.Contains("\n## 2014 (SRD 5.1)\n\n*spell · 2014 · SRD 5.1 · `2014/spell/fireball`*\n*Note on 2014.*\n\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## 2024 (SRD 5.2.1)\n\n*spell · 2024 · SRD 5.2.1 · `2024/spell/fireball`*\n*Note on 2024.*\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_CorrectedDocument_PrintsOneLabelPerReasonDirectlyUnderTheMetaLine()
    {
        // The label is what marks corrected text as not the untouched upstream record; without it the model relays rewritten
        // text as SRD 5.2.1 verbatim.
        var text = SrdMarkdown.Format(Corrected("2024", "  First reason. ", "Second reason."), SrdMarkdown.Concise, Lookup);

        Assert.StartsWith(
            "# Corrected\n*rule · 2024 · SRD 5.2.1 Rules Glossary · `2024/rule/corrected`*\n" +
            "*Corrected from the upstream data: First reason.*\n*Corrected from the upstream data: Second reason.*\n\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_CorrectedSide_IsLabelledOnThatSideOnly()
    {
        var text = SrdMarkdown.Compare(Corrected("2014"), Corrected("2024", "Only 2024."), SrdMarkdown.Concise, Lookup);

        Assert.DoesNotContain("Corrected from the upstream data", Between(text, "## 2014 (SRD 5.1)", "## 2024 (SRD 5.2.1)"), StringComparison.Ordinal);
        Assert.Contains(
            "*rule · 2024 · SRD 5.2.1 Rules Glossary · `2024/rule/corrected`*\n*Corrected from the upstream data: Only 2024.*\n",
            Between(text, "## 2024 (SRD 5.2.1)", null),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_MissingSideWithWhereToLookInstead_SaysNothingMatchedThenWhereNotSearch()
    {
        // Level records are not searchable, so "try rules_search" for a missing level side could only fail.
        var text = SrdMarkdown.Compare(
            Lookup.Require("2014/level/fighter-5"), null, SrdMarkdown.Concise, Lookup, note2024: "*Fighter levels are elsewhere.*");

        Assert.EndsWith(
            "## 2024 (SRD 5.2.1)\n\n*No 2024 entry matched this one by a known rename or by name.*\n*Fighter levels are elsewhere.*",
            text,
            StringComparison.Ordinal);
    }

    // A small rule with the given correction reasons.
    private static SrdDocument Corrected(string edition, params string[] reasons) => new()
    {
        Edition = edition,
        Kind = "rule",
        Slug = "corrected",
        Name = "Corrected",
        Json = edition == "2014"
            ? System.Text.Json.JsonSerializer.Serialize(new { index = "corrected", name = "Corrected", desc = "Text.", url = "/api/2014/rules/corrected" })
            : System.Text.Json.JsonSerializer.Serialize(new { name = "Corrected", description = "Text.", tags = Array.Empty<string>() }),
        Corrections = reasons,
    };

    [Fact]
    public void Format_Lead_SitsUnderTheMetaLineBeforeTheBody()
    {
        var text = SrdMarkdown.Format(Lookup.Require("2014/spell/fireball"), SrdMarkdown.Concise, Lookup, lead: "*Why this entry.*");

        Assert.StartsWith("# Fireball\n*spell · 2014 · SRD 5.1 · `2014/spell/fireball`*\n*Why this entry.*\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_CappedDocumentWithATrailer_KeepsTheTrailerWholeWithinTheCap()
    {
        // rules_get passes its notes ("Also named …") in as the trailer so the cap accounts for them; appended afterwards they
        // pushed a capped result past MaxChars, or were cut off with the pointer to the other matches.
        var trailer = "Also named \"Huge\": " + string.Join(", ", Enumerable.Range(1, 10).Select(i => $"`2014/rule/huge-{i}`")) + ". " + new string('n', 400);

        var text = SrdMarkdown.Format(HugeRule("2014"), SrdMarkdown.Concise, Lookup, trailer);

        Assert.True(text.Length <= SrdMarkdown.MaxChars, $"{text.Length} characters.");
        Assert.EndsWith(trailer, text, StringComparison.Ordinal);
        Assert.Contains("[Truncated: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_CappedComparisonWithATrailer_KeepsTheTrailerWholeWithinTheCap()
    {
        var trailer = "Huge (`2014/rule/huge`) also corresponds to " + new string('m', 600) + ".";

        var text = SrdMarkdown.Compare(HugeRule("2014"), HugeRule("2024"), SrdMarkdown.Concise, Lookup, trailer);

        Assert.True(text.Length <= SrdMarkdown.MaxChars, $"{text.Length} characters.");
        Assert.EndsWith(trailer, text, StringComparison.Ordinal);
        Assert.Contains("[Truncated: ", text, StringComparison.Ordinal);
    }

    // A rule far over the cap, so any trailer handling shows in the length.
    private static SrdDocument HugeRule(string edition)
    {
        var paragraphs = Enumerable.Range(1, 1_200).Select(i => $"Paragraph {i} of a rule that is much longer than any real one.");
        var body = string.Join("\n\n", paragraphs);
        var json = edition == "2014"
            ? System.Text.Json.JsonSerializer.Serialize(new { index = "huge", name = "Huge", desc = body, url = "/api/2014/rules/huge" })
            : System.Text.Json.JsonSerializer.Serialize(new { name = "Huge", description = body, tags = Array.Empty<string>() });
        return new SrdDocument { Edition = edition, Kind = "rule", Slug = "huge", Name = "Huge", Json = json };
    }

    [Fact]
    public void Compare_KindWithoutGlance_GoesStraightToTheEditions()
    {
        var text = Compare("2014/condition/grappled", "2024/condition/grappled");

        Assert.StartsWith("# Grappled — 2014 vs 2024\n\n## 2014 (SRD 5.1)\n\n", text, StringComparison.Ordinal);
        Assert.Equal(new[] { "# Grappled — 2014 vs 2024", "## 2014 (SRD 5.1)", "## 2024 (SRD 5.2.1)" }, TopHeadings(text));
    }

    [Fact]
    public void Compare_FullFormat_ShowsEachEditionsRawRecordUnderItsOwnHeading()
    {
        var text = SrdMarkdown.Compare(Lookup.Require("2014/spell/fireball"), Lookup.Require("2024/spell/fireball"), SrdMarkdown.Full, Lookup);

        Assert.Contains("### Raw data", Between(text, "## 2014 (SRD 5.1)", "## 2024 (SRD 5.2.1)"), StringComparison.Ordinal);
        Assert.Contains("### Raw data", Between(text, "## 2024 (SRD 5.2.1)", null), StringComparison.Ordinal);
        Assert.Contains("\"url\": \"/api/2024/spells/fireball\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_NeitherEdition_Throws()
    {
        Assert.Throws<ArgumentException>(() => SrdMarkdown.Compare(null, null, SrdMarkdown.Concise, Lookup));
    }

    [Theory]
    [InlineData("2014/monster/lich", "2024/monster/lich")]
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon")]
    [InlineData("2014/spell/animate-objects", "2024/spell/animate-objects")]
    public void Compare_LargeRealPair_StaysUntruncatedWithOnlyTheShapesHeadings(string reference2014, string reference2024)
    {
        var text = Compare(reference2014, reference2024);

        Assert.True(text.Length <= SrdMarkdown.MaxChars, $"{text.Length} characters.");
        Assert.DoesNotContain("[Truncated", text, StringComparison.Ordinal);
        Assert.Equal(4, TopHeadings(text).Count);
    }

    private static string Compare(string? reference2014, string? reference2024) =>
        SrdMarkdown.Compare(
            reference2014 is null ? null : Lookup.Require(reference2014),
            reference2024 is null ? null : Lookup.Require(reference2024),
            SrdMarkdown.Concise,
            Lookup);

    // Every "# " and "## " line, in order.
    private static List<string> TopHeadings(string text) =>
        text.Split('\n').Where(l => l.StartsWith("# ", StringComparison.Ordinal) || l.StartsWith("## ", StringComparison.Ordinal)).ToList();

    private static string Between(string text, string start, string? end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"\"{start}\" not found.");
        var to = end is null ? text.Length : text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to >= 0, $"\"{end}\" not found after \"{start}\".");
        return text[from..to];
    }

    [Fact]
    public void Cap_ShortTextWithTrailer_AppendsTrailerAfterABlankLine()
    {
        var result = SrdMarkdown.Cap("# Fireball\nbody", [new SrdRef("2024", "spell", "fireball")], "Also named \"x\".");

        Assert.Equal("# Fireball\nbody\n\nAlso named \"x\".", result);
    }

    [Fact]
    public void Cap_TextThatFitsOnlyWithoutTheTrailer_CutsTheTextAndKeepsTheTrailerWhole()
    {
        // The text alone fits; with the trailer it would not. Appending the trailer after capping went over the budget.
        var text = string.Concat(Enumerable.Repeat("line of rules text\n", (SrdMarkdown.MaxChars - 100) / 19));
        var trailer = "Also named \"Shield\": `2024/equipment/shield`. " + new string('n', 600);

        var result = SrdMarkdown.Cap(text, [new SrdRef("2024", "spell", "shield")], trailer);

        Assert.True(result.Length <= SrdMarkdown.MaxChars, $"{result.Length} characters.");
        Assert.EndsWith(trailer, result, StringComparison.Ordinal);
        Assert.Contains("[Truncated: ", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Cap_TruncatedComparison_NamesBothEditionsRefs()
    {
        var text = new string('x', 10) + "\n" + string.Concat(Enumerable.Repeat("y\n", SrdMarkdown.MaxChars));

        var result = SrdMarkdown.Cap(text, [new SrdRef("2014", "rule", "combat"), new SrdRef("2024", "rule", "attack")]);

        Assert.Contains("of `2014/rule/combat` and `2024/rule/attack` not shown", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Cap_CutInsideCodeFence_ClosesTheFenceBeforeTheNote()
    {
        var text = "### Raw data\n\n```json\n" + string.Concat(Enumerable.Repeat("  \"k\": 1,\n", SrdMarkdown.MaxChars / 8));

        var result = SrdMarkdown.Cap(text, [new SrdRef("2014", "monster", "lich")]);

        var noteAt = result.IndexOf("*[Truncated", StringComparison.Ordinal);
        Assert.Equal("\n```\n\n", result.Substring(noteAt - 6, 6));
    }
}
