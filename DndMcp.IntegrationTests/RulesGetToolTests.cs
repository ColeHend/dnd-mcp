using DndMcp.Formatting.Srd;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>rules_get</c> through the real client returns exactly the entry the call names — by ref in every form a
/// ref is printed or copied, or by name in the edition asked for — rendered as one markdown document; <c>both</c>
/// returns the entry beside its other-edition counterpart (or, with none recorded, the other edition's entry with the same
/// slug or name, flagged when only the name matched), and says a side has nothing only as a search that failed; and every
/// miss or conflict is an error that says what to send instead, with refs that resolve.
///
/// <para>
/// The failures here are quiet ones. A ref resolved in the wrong edition returns real SRD text for the wrong game; an
/// ambiguous name that silently picks one entry hides the armor behind the spell; a comparison against the wrong
/// counterpart reads as an authoritative "what changed". Rendering itself belongs to the formatters (their own tests
/// pin every line), so these tests pin what the tool decides: which document, which edition, which note is added.
/// </para>
/// </summary>
public sealed class RulesGetToolTests : IClassFixture<McpServerHarness>
{
    private const string Tool = "rules_get";
    private const string Prefix = "An error occurred invoking 'rules_get': ";
    private const string Examples = "Example: {\"ref\":\"2024/spell/fireball\"} or {\"name\":\"Fireball\"}.";

    // What a comparison says for a side nothing matched: a search that failed, never "the SRD has no such rule".
    private const string No2014Match =
        "*No 2014 entry matched this one by a known rename or by name. The 2014 SRD may cover it under another heading — try " +
        "rules_search with edition 2014.*";

    private const string No2024Match =
        "*No 2024 entry matched this one by a known rename or by name. The 2024 SRD may cover it under another heading — try " +
        "rules_search with edition 2024.*";

    private readonly McpServerHarness _server;

    public RulesGetToolTests(McpServerHarness server)
    {
        _server = server;
    }

    [Theory]
    [InlineData("""{"name":"Fireball"}""", "Fireball", "*spell · 2024 · SRD 5.2.1 · `2024/spell/fireball`*")]
    [InlineData("""{"name":"fireball","edition":"2014"}""", "Fireball", "*spell · 2014 · SRD 5.1 · `2014/spell/fireball`*")]
    [InlineData("""{"name":"Adult Red Dragon"}""", "Adult Red Dragon", "*monster · 2024 · SRD 5.2.1 · `2024/monster/adult-red-dragon`*")]
    [InlineData("""{"name":"Grappled","edition":"2014"}""", "Grappled", "*condition · 2014 · SRD 5.1 · `2014/condition/grappled`*")]
    [InlineData("""{"name":"Fighter 5","edition":"2014"}""", "Fighter 5", "*level · 2014 · SRD 5.1 · `2014/level/fighter-5`*")]
    [InlineData("""{"name":"Finesse","kind":"rule"}""", "Finesse (Weapon Property)", "*rule · 2024 · SRD 5.2.1 Rules Glossary · `2024/rule/finesse-weapon-property`*")]
    public async Task CallTool_Name_RendersThatEntryInTheEditionAsked(string argumentsJson, string title, string metaLine)
    {
        var lines = await GetLinesAsync(argumentsJson);

        Assert.Equal("# " + title, lines[0]);
        Assert.Equal(metaLine, lines[1]);
    }

    [Theory]
    // The other edition's name for a renamed entry finds it, and the meta line says why it matched.
    [InlineData("""{"name":"Thug"}""", "# Tough", "`2024/monster/tough`", "2014 name: Thug")]
    [InlineData("""{"name":"Tough","edition":"2014"}""", "# Thug", "`2014/monster/thug`", "2024 name: Tough")]
    [InlineData("""{"name":"Lore","kind":"subclass"}""", "# College of Lore", "`2024/subclass/college-of-lore`", "2014 name: Lore")]
    [InlineData("""{"name":"College of Lore","kind":"subclass","edition":"2014"}""", "# Lore", "`2014/subclass/lore`", "2024 name: College of Lore")]
    public async Task CallTool_OtherEditionsName_FindsTheRenamedEntry(string argumentsJson, string title, string reference, string nameLine)
    {
        var lines = await GetLinesAsync(argumentsJson);

        Assert.Equal(title, lines[0]);
        Assert.EndsWith(reference + "*", lines[1], StringComparison.Ordinal);
        // The other edition's name is labelled with its edition: "Also known as: Thug" read as a 2024 name for Tough.
        Assert.Equal(nameLine, lines[2]);
    }

    [Theory]
    [InlineData("2024/spell/fireball", null, "2024/spell/fireball")]
    [InlineData("spell/fireball", null, "2024/spell/fireball")]
    [InlineData("spell/fireball", "2014", "2014/spell/fireball")]
    [InlineData("spells/fireball", "2014", "2014/spell/fireball")]
    [InlineData("`2014/spell/fireball`", null, "2014/spell/fireball")]
    [InlineData("/api/2014/spells/fireball", null, "2014/spell/fireball")]
    [InlineData("https://www.dnd5eapi.co/api/2014/spells/fireball", null, "2014/spell/fireball")]
    [InlineData("/api/2014/classes/fighter/levels/5", null, "2014/level/fighter-5")]
    [InlineData("2014/level/fighter-5", null, "2014/level/fighter-5")]
    // race is 2014's word for 2024's species; each edition resolves it to its own kind.
    [InlineData("2024/race/elf", null, "2024/species/elf")]
    [InlineData("2024/rule/finesse-weapon-property", null, "2024/rule/finesse-weapon-property")]
    public async Task CallTool_Ref_FetchesExactlyThatEntry(string reference, string? edition, string expected)
    {
        var arguments = new Dictionary<string, object?> { ["ref"] = reference };
        if (edition is not null)
        {
            arguments["edition"] = edition;
        }

        var text = _server.SuccessText(await _server.Client.CallToolAsync(Tool, arguments));

        Assert.EndsWith($" · `{expected}`*", text.Split('\n')[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("spell/fireball", "spell")]
    [InlineData("spell/fireball", "Spells")]
    [InlineData("2024/species/elf", "race")]
    public async Task CallTool_RefWithMatchingKind_IsAccepted(string reference, string kind)
    {
        var text = _server.SuccessText(await _server.Client.CallToolAsync(
            Tool, new Dictionary<string, object?> { ["ref"] = reference, ["kind"] = kind }));

        Assert.StartsWith("# ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_SharedName_RendersTheLikeliestAndListsTheOthers()
    {
        var text = await GetTextAsync("""{"name":"Shield"}""");

        Assert.StartsWith("# Shield\n*spell · 2024 · SRD 5.2.1 · `2024/spell/shield`*\n", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "\n\nAlso named \"Shield\": `2024/magic-item/shield`, `2024/equipment/shield`. Pass one as ref, or add kind, to get it instead.",
            text,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("magic-item", "`2024/magic-item/shield`")]
    [InlineData("equipment", "`2024/equipment/shield`")]
    [InlineData("spells", "`2024/spell/shield`")]
    public async Task CallTool_SharedNameWithKind_RendersThatKindOnly(string kind, string reference)
    {
        var text = await GetTextAsync($$"""{"name":"Shield","kind":"{{kind}}"}""");

        Assert.EndsWith(reference + "*", text.Split('\n')[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Also named", text, StringComparison.Ordinal);
    }

    [Theory]
    // kind was given: adding it again cannot narrow anything.
    [InlineData("""{"name":"Channel Divinity","kind":"feature"}""",
        "\n\nAlso named \"Channel Divinity\": `2024/feature/paladin-channel-divinity` (Paladin 3). Pass one as ref to get it instead.")]
    // Every other match is a feature too, so kind cannot tell them apart; the class and level can.
    [InlineData("""{"name":"Extra Attack"}""",
        "\n\nAlso named \"Extra Attack\": `2024/feature/fighter-extra-attack` (Fighter 5), `2024/feature/monk-extra-attack` (Monk 5), " +
        "`2024/feature/paladin-extra-attack` (Paladin 5), `2024/feature/ranger-extra-attack` (Ranger 5). Pass one as ref to get it instead.")]
    public async Task CallTool_SharedNameThatKindCannotNarrow_DoesNotSuggestAddingKind(string argumentsJson, string footer)
    {
        var text = await GetTextAsync(argumentsJson);

        Assert.EndsWith(footer, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ManySameKindMatches_ListsTenWithTheirClassesAndCountsTheRest()
    {
        var text = await GetTextAsync("""{"name":"Ability Score Improvement","edition":"2014","kind":"feature"}""");

        // Same name, same kind: the lowest class level first, so each class's level-4 record before any class's second.
        Assert.Contains(
            "\n\nAlso named \"Ability Score Improvement\": `2014/feature/bard-ability-score-improvement-1` (Bard 4), " +
            "`2014/feature/cleric-ability-score-improvement-1` (Cleric 4), ",
            text,
            StringComparison.Ordinal);
        Assert.EndsWith(" and 52 more. Pass one as ref to get it instead.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BothByName_ShowsEachEditionsEntryUnderItsOwnHeading()
    {
        // The plan's manual check: "Compare grappled in 2014 and 2024".
        var text = await GetTextAsync("""{"name":"grappled","edition":"both"}""");

        Assert.StartsWith("# Grappled — 2014 vs 2024\n\n## 2014 (SRD 5.1)\n\n*condition · 2014 · SRD 5.1 · `2014/condition/grappled`*\n", text, StringComparison.Ordinal);
        Assert.Contains("\n\n## 2024 (SRD 5.2.1)\n\n*condition · 2024 · SRD 5.2.1 · `2024/condition/grappled`*\n", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "\n\nAlso named \"grappled\": `2024/rule/grappled`. Pass one as ref, or add kind, to get it instead.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"name":"Thug","edition":"both"}""")]
    [InlineData("""{"name":"Tough","edition":"both"}""")]
    [InlineData("""{"ref":"2014/monster/thug","edition":"both"}""")]
    [InlineData("""{"ref":"monster/tough","edition":"both"}""")]
    public async Task CallTool_BothForARenamedEntry_PairsItWithItsCounterpart(string argumentsJson)
    {
        var text = await GetTextAsync(argumentsJson);

        Assert.StartsWith("# Thug (2014) / Tough (2024) — 2014 vs 2024\n\n## At a glance\n", text, StringComparison.Ordinal);
        Assert.Contains("*monster · 2014 · SRD 5.1 · `2014/monster/thug`*", text, StringComparison.Ordinal);
        Assert.Contains("*monster · 2024 · SRD 5.2.1 · `2024/monster/tough`*", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"name":"Duergar","edition":"both"}""", "## 2014 (SRD 5.1)\n\n*monster · 2014 · SRD 5.1 · `2014/monster/duergar`*", "## 2024 (SRD 5.2.1)\n\n" + No2024Match)]
    [InlineData("""{"name":"Allosaurus","edition":"both"}""", "## 2024 (SRD 5.2.1)\n\n*monster · 2024 · SRD 5.2.1 · `2024/monster/allosaurus`*", "## 2014 (SRD 5.1)\n\n" + No2014Match)]
    [InlineData("""{"ref":"2024/monster/allosaurus","edition":"both"}""", "`2024/monster/allosaurus`", "## 2014 (SRD 5.1)\n\n" + No2014Match)]
    // race is 2014's word; with both, a 2024-only species is still found under it (the lookup runs in 2024 first).
    [InlineData("""{"name":"Goliath","kind":"race","edition":"both"}""", "`2024/species/goliath`", "## 2014 (SRD 5.1)\n\n" + No2014Match)]
    public async Task CallTool_BothForAnEntryInOneEdition_ShowsThatSideAndSaysNothingMatchedRatherThanThatNoneExists(
        string argumentsJson, string present, string absent)
    {
        var text = await GetTextAsync(argumentsJson);

        Assert.Contains(present, text, StringComparison.Ordinal);
        Assert.Contains(absent, text, StringComparison.Ordinal);
        Assert.DoesNotContain("equivalent in the SRD", text, StringComparison.Ordinal);
    }

    [Theory]
    // Level records share their slugs across editions: the index pairs them by slug.
    [InlineData("""{"name":"Fighter 5","edition":"both"}""", "`2014/level/fighter-5`", "`2024/level/fighter-5`")]
    [InlineData("""{"ref":"2014/level/fighter-5","edition":"both"}""", "`2014/level/fighter-5`", "`2024/level/fighter-5`")]
    [InlineData("""{"ref":"2024/level/wizard-3","edition":"both"}""", "`2014/level/wizard-3`", "`2024/level/wizard-3`")]
    // Same name in another kind: 2014's Divine Smite is a paladin feature, 2024's is a spell.
    [InlineData("""{"name":"Divine Smite","edition":"both"}""", "`2014/feature/divine-smite`", "`2024/spell/divine-smite`")]
    [InlineData("""{"ref":"2014/feature/divine-smite","edition":"both"}""", "`2014/feature/divine-smite`", "`2024/spell/divine-smite`")]
    // The 2024 equipment record and a 2014 magic item of the same name.
    [InlineData("""{"name":"Potion of Healing","edition":"both"}""", "`2014/magic-item/potion-of-healing", "`2024/")]
    // 2014 numbers a recurring feature (metamagic-1, -2, -3); the same name still reaches it.
    [InlineData("""{"name":"Metamagic","edition":"both"}""", "`2014/feature/metamagic-", "`2024/feature/sorcerer-metamagic`")]
    // A 2024 glossary entry and the 2014 condition of the same name.
    [InlineData("""{"ref":"2024/rule/blinded","edition":"both"}""", "`2014/condition/blinded`", "`2024/rule/blinded`")]
    public async Task CallTool_BothForAnEntryBothEditionsHave_ShowsTheOtherEditionsEntry(
        string argumentsJson, string reference2014, string reference2024)
    {
        // "No 2014 equivalent" for an entry the 2014 SRD has under the same slug or name is a false rules fact the model
        // would relay. These pairs all come from the index's counterparts today (slug, manual and feature pairs); the
        // name fallback for entries it leaves unpaired is pinned by the name-only tests below.
        var text = await GetTextAsync(argumentsJson);

        Assert.Contains(reference2014, Between(text, "## 2014 (SRD 5.1)", "## 2024 (SRD 5.2.1)"), StringComparison.Ordinal);
        Assert.Contains(reference2024, Between(text, "## 2024 (SRD 5.2.1)", null), StringComparison.Ordinal);
        Assert.DoesNotContain("matched this one by a known rename or by name", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BothMatchedOnlyByName_SaysSoUnderThatSide()
    {
        // A same-name match is a guess the model should check: 2014's Camel is the mount's price-list entry, 2024's the
        // creature's stat block.
        var text = await GetTextAsync("""{"ref":"2014/equipment/camel","edition":"both"}""");

        var side2024 = Between(text, "## 2024 (SRD 5.2.1)", null);
        Assert.StartsWith("## 2024 (SRD 5.2.1)\n\n*monster · 2024 · SRD 5.2.1 · `2024/monster/camel`*\n", side2024, StringComparison.Ordinal);
        Assert.Contains(
            "\n*Matched to the 2014 entry by name only (no recorded counterpart links them): check that they are the same thing. " +
            "Its kind differs: equipment in 2014, monster in 2024.*\n\n",
            side2024,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Matched to the 2024 entry", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BothMatchedBySlug_NeedsNoNote()
    {
        // The same kind and slug is the index's own pairing rule; only name matches are guesses worth flagging.
        var text = await GetTextAsync("""{"ref":"2014/level/fighter-5","edition":"both"}""");

        Assert.DoesNotContain("by name only", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BothMatchedByNameWithSeveralCandidates_ListsTheOthers()
    {
        // The 2014 Spellcasting rules section against the nine 2024 class features named Spellcasting: one is shown, and
        // the model is told the rest exist rather than left to think the first is the only one.
        var text = await GetTextAsync("""{"ref":"2014/rule/spellcasting","edition":"both"}""");
        var side2024 = Between(text, "## 2024 (SRD 5.2.1)", null);

        Assert.StartsWith("## 2024 (SRD 5.2.1)\n\n*feature · 2024 · SRD 5.2.1 · `2024/feature/bard-spellcasting`*\n", side2024, StringComparison.Ordinal);
        Assert.Contains("\n*Matched to the 2014 entry by name only", side2024, StringComparison.Ordinal);
        Assert.Contains(
            " Other 2024 entries with this name: `2024/feature/cleric-spellcasting`, `2024/feature/druid-spellcasting`, ",
            side2024,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BothForASplitEntry_ComparesTheFirstAndNamesTheRest()
    {
        // 2014's Succubus/Incubus is two 2024 monsters; the comparison uses one and must not hide the other.
        var text = await GetTextAsync("""{"ref":"2014/monster/succubus-incubus","edition":"both"}""");

        Assert.StartsWith("# Succubus/Incubus (2014) / Incubus (2024) — 2014 vs 2024\n", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "\n\nSuccubus/Incubus (`2014/monster/succubus-incubus`) also corresponds to Succubus (`2024/monster/succubus`) in the " +
            "2024 SRD; pass one as ref with edition \"both\" to compare it.",
            text,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("concise", false)]
    [InlineData("full", true)]
    [InlineData("FULL", true)]
    [InlineData(" full ", true)]
    // Blank means the default, as a blank edition does; refusing it would cost the model a retry for nothing.
    [InlineData("", false)]
    [InlineData("  ", false)]
    public async Task CallTool_Format_FullAddsTheRawRecord(string format, bool raw)
    {
        var text = await GetTextAsync($$"""{"name":"Fireball","format":"{{format}}"}""");

        Assert.StartsWith("# Fireball\n", text, StringComparison.Ordinal);
        Assert.Equal(raw, text.Contains("\n### Raw data\n\n```json\n{", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"name":"Fighter"}""")]
    [InlineData("""{"name":"Fighter","edition":"2014"}""")]
    [InlineData("""{"name":"Wizard"}""")]
    [InlineData("""{"name":"Wizard","edition":"2014"}""")]
    [InlineData("""{"name":"Adult Red Dragon","edition":"both"}""")]
    [InlineData("""{"name":"Lich","edition":"both"}""")]
    [InlineData("""{"name":"Fireball","edition":"both"}""")]
    public async Task CallTool_TypicalLargeEntries_StayUnderTheOutputBudget(string argumentsJson)
    {
        // Claude Code warns at 10,000 tokens; the plan's budget for a typical result is 8,000 (about 4 characters each).
        var text = await GetTextAsync(argumentsJson);

        Assert.True(text.Length / 4 < 8_000, $"{argumentsJson}: {text.Length} characters.");
        Assert.DoesNotContain("[Truncated:", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{}""", "Give either ref or name. " + Examples)]
    [InlineData("""{"name":"  "}""", "Give either ref or name. " + Examples)]
    [InlineData("""{"ref":"","name":null}""", "Give either ref or name. " + Examples)]
    [InlineData("""{"kind":"spell"}""", "Give either ref or name. " + Examples)]
    [InlineData("""{"ref":"spell/fireball","name":"Fireball"}""", "Give ref or name, not both. " + Examples)]
    public async Task CallTool_NotExactlyOneOfRefAndName_SaysSoWithAnExample(string argumentsJson, string message)
    {
        var result = await _server.CallToolJsonAsync(Tool, argumentsJson);

        Assert.Equal(Prefix + message, _server.ErrorText(result));
    }

    [Theory]
    [InlineData("""{"name":"Fireball","format":"short"}""", "format must be " + SrdMarkdown.FormatsText + " (got \"short\").")]
    // combatant is a real format, for monsters only: a spell is refused with the formats, never answered in another one.
    [InlineData("""{"name":"Fireball","format":"combatant"}""",
        "format \"combatant\" is for monsters (a stat block as balance_simulate reads it); `2024/spell/fireball` is a spell. " +
        "Formats: " + SrdMarkdown.FormatsText + ".")]
    [InlineData("""{"ref":"2014/condition/grappled","format":"combatant","edition":"both"}""",
        "format \"combatant\" is for monsters (a stat block as balance_simulate reads it); `2014/condition/grappled` is a condition. " +
        "Formats: " + SrdMarkdown.FormatsText + ".")]
    [InlineData("""{"name":"Fireball","edition":"5.5e"}""", "edition must be \"2014\", \"2024\" or \"both\" (got \"5.5e\").")]
    [InlineData("""{"ref":"spell/fireball","edition":"2025"}""", "edition must be \"2014\", \"2024\" or \"both\" (got \"2025\").")]
    public async Task CallTool_UnknownFormatOrEdition_ListsTheAcceptedValues(string argumentsJson, string message)
    {
        var result = await _server.CallToolJsonAsync(Tool, argumentsJson);

        Assert.Equal(Prefix + message, _server.ErrorText(result));
    }

    [Fact]
    public async Task CallTool_RefFromOneEditionWithTheOtherEdition_RefusesRatherThanGuessing()
    {
        var result = await _server.CallToolJsonAsync(Tool, """{"ref":"2014/spell/fireball","edition":"2024"}""");

        Assert.Equal(
            Prefix + "ref `2014/spell/fireball` is a 2014 entry but edition is \"2024\". Leave edition out to use the ref's own, " +
            "pass \"both\" to compare the editions, or use the ref 2024/spell/fireball.",
            _server.ErrorText(result));
    }

    [Theory]
    // Same slug in the other edition.
    [InlineData("2014/spell/fireball", "2024", "2024/spell/fireball")]
    // Renamed: the recorded counterpart, not the slug swapped into the other edition (2024/monster/thug does not exist).
    [InlineData("2014/monster/thug", "2024", "2024/monster/tough")]
    [InlineData("2024/monster/tough", "2014", "2014/monster/thug")]
    [InlineData("2014/subclass/lore", "2024", "2024/subclass/college-of-lore")]
    // race is species in 2024: the ref offered is the one that edition spells.
    [InlineData("2014/race/elf", "2024", "2024/species/elf")]
    public async Task CallTool_RefFromOneEditionWithTheOtherEdition_OffersARefThatResolves(string reference, string edition, string offered)
    {
        // A model helping with a 2014 game copies a 2024 ref out of rules_search (which defaults to 2024) and asks for
        // edition 2014. The advice must be a ref that exists; following a made-up one costs a call and a second error.
        var error = await GetErrorAsync($$"""{"ref":"{{reference}}","edition":"{{edition}}"}""");

        Assert.EndsWith($", or use the ref {offered}.", error, StringComparison.Ordinal);
        var text = await GetTextAsync($$"""{"ref":"{{offered}}"}""");
        Assert.EndsWith($" · `{offered}`*", text.Split('\n')[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RefOfAKindTheAskedEditionLacks_SaysSoInsteadOfOfferingARef()
    {
        var error = await GetErrorAsync("""{"ref":"2024/poison/serpent-venom","edition":"2014"}""");

        Assert.Equal(
            Prefix + "ref `2024/poison/serpent-venom` is a 2024 entry but edition is \"2014\". Leave edition out to use the ref's " +
            "own, or pass \"both\" to compare the editions. The 2014 SRD has no poison entries.",
            error);
    }

    [Theory]
    [InlineData("""{"ref":"2014/poison/serpent-venom"}""")]
    [InlineData("""{"ref":"2014/poison/serpent-venom","edition":"2024"}""")]
    [InlineData("""{"ref":"2014/poison/serpent-venom","edition":"both"}""")]
    [InlineData("""{"ref":"`2014/weapon-mastery/cleave`"}""")]
    // The API URLs the tool description advertises, and the raw JSON links to, loop the same way.
    [InlineData("""{"ref":"/api/2014/poisons/serpent-venom"}""")]
    [InlineData("""{"ref":"/api/2014/poisons/serpent-venom","edition":"2024"}""")]
    [InlineData("""{"ref":"/api/2014/poisons/serpent-venom","edition":"both"}""")]
    [InlineData("""{"ref":"https://www.dnd5eapi.co/api/2014/weapon-mastery-properties/cleave","edition":"2024"}""")]
    public async Task CallTool_RefNamingAnEditionThatLacksItsKind_OffersTheRefThatWorks(string argumentsJson)
    {
        // The ref's own edition wins over the edition argument, so "pass edition 2024" cannot help: the same error came back
        // for every edition the model tried.
        var error = await GetErrorAsync(argumentsJson);

        var offered = error[(error.LastIndexOf("Use the ref ", StringComparison.Ordinal) + "Use the ref ".Length)..].TrimEnd('.');
        Assert.StartsWith(Prefix + "ref `", error, StringComparison.Ordinal);
        Assert.Contains(" names the 2014 SRD, which has no ", error, StringComparison.Ordinal);
        Assert.StartsWith("2024/", offered, StringComparison.Ordinal);
        var text = await GetTextAsync($$"""{"ref":"{{offered}}"}""");
        Assert.EndsWith($" · `{offered}`*", text.Split('\n')[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RefNamingAnEditionThatLacksItsKind_SaysExactlyWhatToSend()
    {
        var error = await GetErrorAsync("""{"ref":"2014/poison/serpent-venom"}""");

        Assert.Equal(
            Prefix + "ref `2014/poison/serpent-venom` names the 2014 SRD, which has no poison entries; only the 2024 SRD has " +
            "them. Use the ref 2024/poison/serpent-venom.",
            error);
    }

    [Fact]
    public async Task CallTool_RefNamingAnEditionThatLacksItsKindAndNoSuchEntry_OffersCloseNamesNotAMadeUpRef()
    {
        // Every offered ref is looked up first: "Use the ref 2024/poison/no-such-poison" was advice that could only fail.
        var error = await GetErrorAsync("""{"ref":"2014/poison/no-such-poison"}""");

        Assert.StartsWith(
            Prefix + "ref `2014/poison/no-such-poison` names the 2014 SRD, which has no poison entries; only the 2024 SRD has them. " +
            "No 2024 poison has the slug \"no-such-poison\" (`2024/poison/no-such-poison`). Did you mean ",
            error,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Use the ref", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RefWithADifferentKind_SaysARefNamesItsKind()
    {
        var result = await _server.CallToolJsonAsync(Tool, """{"ref":"spell/fireball","kind":"monster"}""");

        Assert.Equal(
            Prefix + "kind \"monster\" does not match ref `2024/spell/fireball`, which is a spell. A ref already names its kind; " +
            "kind only narrows a name.",
            _server.ErrorText(result));
    }

    [Theory]
    [InlineData("""{"ref":"Fireball"}""",
        "'Fireball' is not a rules ref. Use kind/slug (spell/fireball), edition/kind/slug (2024/spell/fireball) or an API URL " +
        "(/api/2024/spells/fireball). To look an entry up by its name, pass name \"Fireball\" instead.")]
    [InlineData("""{"ref":"a/b/c/d"}""",
        "'a/b/c/d' is not a rules ref. Use kind/slug (spell/fireball), edition/kind/slug (2024/spell/fireball) or an API URL " +
        "(/api/2024/spells/fireball).")]
    [InlineData("""{"name":"2024/spell/fireball"}""",
        "Nothing in the 2024 SRD is named \"2024/spell/fireball\". \"2024/spell/fireball\" looks like a ref: pass it as ref instead of name.")]
    public async Task CallTool_RefAndNameMixedUp_SaysWhichArgumentToUse(string argumentsJson, string message)
    {
        var result = await _server.CallToolJsonAsync(Tool, argumentsJson);

        Assert.Equal(Prefix + message, _server.ErrorText(result));
    }

    [Fact]
    public async Task CallTool_NameOnlyInTheOtherEdition_SaysWhereItIs()
    {
        // "Nothing … is named", not "Not in the 2024 SRD": a name lookup cannot know the SRD lacks the rule under another
        // heading, and the model relays whichever it is told.
        var result = await _server.CallToolJsonAsync(Tool, """{"name":"Duergar"}""");

        Assert.Equal(
            Prefix + "Nothing in the 2024 SRD is named \"Duergar\"; the 2014 SRD has Duergar (`2014/monster/duergar`) — pass " +
            "edition 2014 or both. rules_search finds entries by words.",
            _server.ErrorText(result));
    }

    [Fact]
    public async Task CallTool_2024OnlyNameAsked2014_SaysWhereItIs()
    {
        var result = await _server.CallToolJsonAsync(Tool, """{"name":"Allosaurus","edition":"2014"}""");

        Assert.StartsWith(
            Prefix + "Nothing in the 2014 SRD is named \"Allosaurus\"; the 2024 SRD has Allosaurus (`2024/monster/allosaurus`) — pass " +
            "edition 2024 or both.",
            _server.ErrorText(result),
            StringComparison.Ordinal);
    }

    [Theory]
    // A typo of a 2014-only entry: the 2024 lookup has nothing close, so without the 2014 lookup there was no suggestion.
    [InlineData("Duergr", "`2014/monster/duergar`")]
    [InlineData("Feeblemnd", "`2014/spell/feeblemind`")]
    public async Task CallTool_BothTypoOfA2014OnlyName_OffersTheClose2014Name(string name, string reference)
    {
        var error = await GetErrorAsync($$"""{"name":"{{name}}","edition":"both"}""");

        Assert.StartsWith(Prefix + $"Nothing in the 2014 or 2024 SRD is named \"{name}\".", error, StringComparison.Ordinal);
        Assert.Contains($" Close names in the 2014 SRD: ", error, StringComparison.Ordinal);
        Assert.Contains(reference, error, StringComparison.Ordinal);
        Assert.EndsWith(" rules_search with edition \"both\" finds entries by words.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BothTypoOfA2024Name_StillOffersThe2024Name()
    {
        var error = await GetErrorAsync("""{"name":"Fierball","edition":"both"}""");

        Assert.Contains(" Close names in the 2024 SRD: Fireball (`2024/spell/fireball`)", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"name":"Fierball"}""",
        "Nothing in the 2024 SRD is named \"Fierball\". Close names in the 2024 SRD: Fireball (`2024/spell/fireball`). " +
        "rules_search finds entries by words.")]
    [InlineData("""{"name":"Fireball","kind":"monster"}""",
        "Nothing in the 2024 SRD's monster entries is named \"Fireball\". Close names in the 2024 SRD: Fire Giant " +
        "(`2024/monster/fire-giant`), Fire Elemental (`2024/monster/fire-elemental`), Giant Fire Beetle " +
        "(`2024/monster/giant-fire-beetle`). rules_search finds entries by words.")]
    public async Task CallTool_NameNotFound_OffersCloseNames(string argumentsJson, string message)
    {
        var result = await _server.CallToolJsonAsync(Tool, argumentsJson);

        Assert.Equal(Prefix + message, _server.ErrorText(result));
    }

    [Theory]
    [InlineData("""{"ref":"spell/fire-ball"}""",
        "No 2024 spell has the slug \"fire-ball\" (`2024/spell/fire-ball`). Did you mean Fireball (`2024/spell/fireball`) or " +
        "Fire Bolt (`2024/spell/fire-bolt`)? rules_search finds entries by words.")]
    [InlineData("""{"ref":"2024/monster/duergar"}""",
        "No 2024 monster has the slug \"duergar\" (`2024/monster/duergar`). The 2014 SRD has Duergar (`2014/monster/duergar`) — " +
        "pass that ref, or edition \"both\" with it to compare. rules_search finds entries by words.")]
    // A ref built from a renamed entry's 2014 slug: the slug read as a name reaches the 2024 entry through its alias.
    [InlineData("""{"ref":"2024/monster/thug"}""",
        "No 2024 monster has the slug \"thug\" (`2024/monster/thug`). Did you mean Thug (2014 name of Tough, `2024/monster/tough`)? The 2014 SRD has " +
        "Thug (`2014/monster/thug`) — pass that ref, or edition \"both\" with it to compare. rules_search finds entries by words.")]
    public async Task CallTool_RefNotFound_OffersTheCloseEntryOrTheOtherEdition(string argumentsJson, string message)
    {
        var result = await _server.CallToolJsonAsync(Tool, argumentsJson);

        Assert.Equal(Prefix + message, _server.ErrorText(result));
    }

    [Fact]
    public async Task CallTool_RefWithAHugeSlug_EchoesItShortened()
    {
        // The slug used to come back twice, whole: a pasted page as a ref returned an error twice the page's size.
        var slug = new string('a', 300);

        var error = await GetErrorAsync($$"""{"ref":"2024/spell/{{slug}}"}""");

        Assert.StartsWith(Prefix + $"No 2024 spell has the slug \"{slug[..80]}…\" (`2024/spell/{slug[..69]}…`).", error, StringComparison.Ordinal);
        Assert.True(error.Length < 400, $"{error.Length} characters.");
    }

    [Theory]
    // Level records are not searchable, so "rules_search finds entries by words" would send the model to a dead end.
    [InlineData("""{"ref":"2014/level/fighter-21"}""",
        "No 2014 level has the slug \"fighter-21\" (`2014/level/fighter-21`). Fighter levels in the 2014 SRD run 1–20, e.g. " +
        "`2014/level/fighter-20`; rules_get name \"Fighter\" shows the whole table.")]
    [InlineData("""{"ref":"2014/level/berserker-4"}""",
        "No 2014 level has the slug \"berserker-4\" (`2014/level/berserker-4`). Berserker levels in the 2014 SRD are 3, 6, 10 " +
        "and 14, e.g. `2014/level/berserker-3`; rules_get name \"Berserker\" shows them all.")]
    // 2024 subclass level slugs use a short name ("berserker-3" for Path of the Berserker); the generic "{subclass}-{level}"
    // advice sent the model to 2024/level/path-of-the-berserker-3, a second miss.
    [InlineData("""{"ref":"2024/level/berserker-4"}""",
        "No 2024 level has the slug \"berserker-4\" (`2024/level/berserker-4`). Path of the Berserker levels in the 2024 SRD are " +
        "3, 6, 10 and 14, e.g. `2024/level/berserker-3`; rules_get name \"Path of the Berserker\" shows them all.")]
    [InlineData("""{"ref":"2024/level/devotion-4"}""",
        "No 2024 level has the slug \"devotion-4\" (`2024/level/devotion-4`). Oath of Devotion levels in the 2024 SRD are 3, 7, " +
        "15 and 20, e.g. `2024/level/devotion-3`; rules_get name \"Oath of Devotion\" shows them all.")]
    [InlineData("""{"ref":"2024/level/oath-of-devotion-3"}""",
        "No 2024 level has the slug \"oath-of-devotion-3\" (`2024/level/oath-of-devotion-3`). Oath of Devotion levels in the 2024 " +
        "SRD are 3, 7, 15 and 20, e.g. `2024/level/devotion-3`; rules_get name \"Oath of Devotion\" shows them all.")]
    [InlineData("""{"ref":"2024/level/nobody-5"}""",
        "No 2024 level has the slug \"nobody-5\" (`2024/level/nobody-5`). Level refs are {class}-{level} (e.g. " +
        "`2024/level/fighter-5`) or a subclass's level slug (e.g. `2024/level/berserker-3`); rules_get with a class's or " +
        "subclass's name shows its levels.")]
    public async Task CallTool_LevelRefNotFound_SaysHowLevelRefsAreFormedInsteadOfPointingAtSearch(string argumentsJson, string message)
    {
        Assert.Equal(Prefix + message, await GetErrorAsync(argumentsJson));
    }

    [Theory]
    // For a name, the edition argument decides, so passing edition 2024 is advice that works.
    [InlineData("""{"name":"Serpent Venom","kind":"poison","edition":"2014"}""",
        "Kind 'poison' is not in the 2014 SRD; only the 2024 SRD has it. Pass edition 2024 (or both).")]
    [InlineData("""{"ref":"poison/serpent-venom","edition":"2014"}""",
        "Kind 'poison' is not in the 2014 SRD; only the 2024 SRD has it. Pass edition 2024 (or both).")]
    public async Task CallTool_KindTheEditionLacks_NamesTheEditionThatHasIt(string argumentsJson, string message)
    {
        var result = await _server.CallToolJsonAsync(Tool, argumentsJson);

        Assert.Equal(Prefix + message, _server.ErrorText(result));
    }

    [Theory]
    // 2024 Swarm of Insects has five 2014 counterparts; the one the caller named is the one compared. Comparing the first
    // put Swarm of Beetles' burrow and climb speeds under a question about wasps.
    [InlineData("""{"name":"Swarm of Wasps","edition":"both"}""", "`2014/monster/swarm-of-wasps`", "`2024/monster/swarm-of-insects`")]
    [InlineData("""{"name":"Swarm of Spiders","edition":"both"}""", "`2014/monster/swarm-of-spiders`", "`2024/monster/swarm-of-insects`")]
    [InlineData("""{"name":"Swarm of Insects","edition":"both"}""", "`2014/monster/swarm-of-insects`", "`2024/monster/swarm-of-insects`")]
    // By ref nothing was typed: the same-slug record comes first.
    [InlineData("""{"ref":"2024/monster/swarm-of-insects","edition":"both"}""", "`2014/monster/swarm-of-insects`", "`2024/monster/swarm-of-insects`")]
    // The exact name of one 2014 grade is that grade, not the lowest one.
    [InlineData("""{"name":"Wild Shape (CR 1 or below)","edition":"both"}""", "`2014/feature/wild-shape-cr-1-or-below`", "`2024/feature/druid-wild-shape`")]
    // A bare graded name is the grade a class gains first.
    [InlineData("""{"name":"Wild Shape","edition":"both"}""", "`2014/feature/wild-shape-cr-1-4-or-below-no-flying-or-swim-speed`", "`2024/feature/druid-wild-shape`")]
    [InlineData("""{"name":"Bardic Inspiration","edition":"both"}""", "`2014/feature/bardic-inspiration-d6`", "`2024/feature/bard-bardic-inspiration`")]
    // A 2014 heading two 2024 entries split: the one whose text is about it (Food is Malnutrition, Water is Dehydration).
    [InlineData("""{"name":"Food","edition":"both"}""", "`2014/rule/food-and-water`", "`2024/rule/malnutrition`")]
    [InlineData("""{"name":"Water","edition":"both"}""", "`2014/rule/food-and-water`", "`2024/rule/dehydration`")]
    // The 2014 heading of a section 2024 split three ways: the 2024 shove is part of Unarmed Strike, not Grappling.
    [InlineData("""{"name":"Shoving a Creature","edition":"both"}""", "`2014/rule/melee-attacks`", "`2024/rule/unarmed-strike`")]
    public async Task CallTool_BothByTheNameOfOneOfSeveralCounterparts_ComparesTheOneNamed(
        string argumentsJson, string reference2014, string reference2024)
    {
        var text = await GetTextAsync(argumentsJson);

        Assert.Contains(reference2014 + "*", FirstMetaLine(Between(text, "## 2014 (SRD 5.1)", "## 2024 (SRD 5.2.1)")), StringComparison.Ordinal);
        Assert.Contains(reference2024 + "*", FirstMetaLine(Between(text, "## 2024 (SRD 5.2.1)", null)), StringComparison.Ordinal);
    }

    [Theory]
    // A literal subsection heading.
    [InlineData("""{"name":"Grappling","edition":"2014"}""", "*Grappling is covered by this entry's #### Grappling subsection.*")]
    // A 2024 name the 2014 section covers: inside a heading that names several things, a run-in paragraph, a plural
    // heading, or no heading at all (Melee Attacks mentions unarmed strikes in one sentence).
    [InlineData("""{"name":"Climbing","edition":"2014"}""", "*Climbing is covered by this entry's #### Climbing, Swimming, and Crawling subsection.*")]
    [InlineData("""{"name":"Long Jump","edition":"2014"}""", "*Long Jump is covered by this entry's Long Jump paragraph.*")]
    [InlineData("""{"name":"Serpent Venom","edition":"2014"}""", "*Serpent Venom is covered by this entry's Serpent Venom (Injury) paragraph.*")]
    [InlineData("""{"name":"Bonus Action","edition":"2014"}""", "*Bonus Action is covered by this entry's #### Bonus Actions subsection.*")]
    [InlineData("""{"name":"Critical Hit","edition":"2014"}""", "*Critical Hit is covered by this entry's #### Critical Hits subsection.*")]
    [InlineData("""{"name":"Unarmed Strike","edition":"2014"}""", "*Unarmed Strike is covered by this entry.*")]
    // Curated everyday names.
    [InlineData("""{"name":"Shove","edition":"2014"}""", "*Shove is covered by this entry's #### Shoving a Creature subsection.*")]
    [InlineData("""{"name":"Shove"}""", "*Shove is covered by this entry.*")]
    [InlineData("""{"name":"Damage Resistance"}""", "*Damage Resistance is covered by this entry.*")]
    // A loose form ("rule" dropped) still says which subsection matched.
    [InlineData("""{"name":"Grappling rule","edition":"2014"}""", "*Grappling is covered by this entry's #### Grappling subsection.*")]
    public async Task CallTool_NameThatIsPartOfAnEntry_SaysTheEntryCoversItAndWhereOnlyIfTheTextHasIt(string argumentsJson, string note)
    {
        var lines = await GetLinesAsync(argumentsJson);

        Assert.Contains(note, lines.Skip(1).TakeWhile(l => l.Length > 0));
    }

    [Theory]
    // 2014 Melee Attacks reached through the name of one 2024 entry that covers part of it: the 2014 side says which
    // part, even when the typed name was a loose form.
    [InlineData("""{"name":"Grappling","edition":"both"}""", "*Grappling is covered by this entry's #### Grappling subsection.*")]
    [InlineData("""{"name":"Grappling rule","edition":"both"}""", "*Grappling is covered by this entry's #### Grappling subsection.*")]
    [InlineData("""{"name":"Unarmed Strike","edition":"both"}""", "*Unarmed Strike is covered by this entry.*")]
    [InlineData("""{"ref":"2024/rule/climbing","edition":"both"}""", "*Climbing is covered by this entry's #### Climbing, Swimming, and Crawling subsection.*")]
    // A 2014-only heading (2024 has no "Hiding" entry): the note sits on the 2014 side, where the name was found.
    [InlineData("""{"name":"Hiding","edition":"both"}""", "*Hiding is covered by this entry's #### Hiding subsection.*")]
    public async Task CallTool_BothReaching2014SectionByAPartOfIt_Notes2014SideWithThatPart(string argumentsJson, string note)
    {
        var side2014 = Between(await GetTextAsync(argumentsJson), "## 2014 (SRD 5.1)", "## 2024 (SRD 5.2.1)");

        Assert.Contains("\n" + note + "\n", side2014, StringComparison.Ordinal);
    }

    [Theory]
    // Only literal headings, separated by "; ": a heading may hold commas, and ", " made "Climbing, Swimming, and
    // Crawling" read as three subsections.
    [InlineData("2014/rule/special-types-of-movement", "Covers: Climbing, Swimming, and Crawling; Jumping")]
    [InlineData("2014/rule/melee-attacks", "Covers: Opportunity Attacks; Two-Weapon Fighting; Contests in Combat; Grappling; Shoving a Creature")]
    public async Task CallTool_2014RuleWithSubsectionAliases_ListsWhatItCoversUnderTheMetaLine(string reference, string covers)
    {
        var lines = await GetLinesAsync($$"""{"ref":"{{reference}}"}""");

        Assert.Equal(covers, lines[2]);
    }

    [Theory]
    // A name found only inside another edition's entry says it is a part of that entry: "the 2014 SRD has Creating
    // Sentient Magic Items" for "Senses" read as the answer.
    [InlineData("""{"name":"Instant Death"}""",
        "Nothing in the 2024 SRD is named \"Instant Death\"; the 2014 SRD has Instant Death (in Dropping to 0 Hit Points, " +
        "`2014/rule/dropping-to-0-hit-points`) — pass edition 2014 or both.")]
    // A suggestion close to a heading says whose heading it is; shown only as "Melee Attacks" it looked unrelated.
    [InlineData("""{"name":"Grapling","edition":"2014"}""",
        "Nothing in the 2014 SRD is named \"Grapling\". Close names in the 2014 SRD: Grappling (in Melee Attacks, " +
        "`2014/rule/melee-attacks`).")]
    // A suggestion close to the other edition's name says so.
    [InlineData("""{"name":"Thugg"}""",
        "Nothing in the 2024 SRD is named \"Thugg\". Close names in the 2024 SRD: Thug (2014 name of Tough, `2024/monster/tough`).")]
    public async Task CallTool_NameNotFoundButCloseToAnotherName_SaysWhatThatNameIs(string argumentsJson, string start)
    {
        Assert.StartsWith(Prefix + start, await GetErrorAsync(argumentsJson), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_EditionConflictMatchedByNameOnly_SaysSoAndThatTheKindDiffers()
    {
        // The same match shown with edition "both" carries the name-only caveat; the conflict's "use the ref" must too, or
        // the 2014 Darkness spell is offered as the 2014 version of the 2024 lighting rule.
        var error = await GetErrorAsync("""{"ref":"2024/rule/darkness","edition":"2014"}""");

        Assert.Equal(
            Prefix + "ref `2024/rule/darkness` is a 2024 entry but edition is \"2014\". Leave edition out to use the ref's own, " +
            "pass \"both\" to compare the editions, or use the ref 2014/spell/darkness, which was matched by name only (a spell " +
            "there, not a rule): check it is the same thing.",
            error);
    }

    [Fact]
    public async Task CallTool_EditionConflictOnASplitEntry_OffersEveryCounterpart()
    {
        var error = await GetErrorAsync("""{"ref":"2014/monster/succubus-incubus","edition":"2024"}""");

        Assert.EndsWith(", or use one of the refs 2024/monster/incubus, 2024/monster/succubus.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_EditionConflictOnARefThatDoesNotExist_AnswersNotFoundWithTheCloseName()
    {
        // Called "a 2014 entry" with no did-you-mean, the typo was never pointed out.
        var error = await GetErrorAsync("""{"ref":"2014/spell/fireballx","edition":"2024"}""");

        Assert.StartsWith(
            Prefix + "No 2014 spell has the slug \"fireballx\" (`2014/spell/fireballx`). Did you mean Fireball (`2014/spell/fireball`)?",
            error,
            StringComparison.Ordinal);
    }

    [Theory]
    // Level records nothing in the other edition matches: where that edition's levels of the same subclass are, not a
    // search, which never returns level records.
    [InlineData("""{"ref":"2014/level/draconic-1","edition":"both"}""", "## 2024 (SRD 5.2.1)",
        "*Draconic Sorcery levels in the 2024 SRD are 3, 6, 14 and 18, e.g. `2024/level/draconic-sorcery-3`; rules_get name " +
        "\"Draconic Sorcery\" shows them all.*")]
    [InlineData("""{"ref":"2024/level/draconic-sorcery-3","edition":"both"}""", "## 2014 (SRD 5.1)",
        "*Draconic levels in the 2014 SRD are 1, 6, 14 and 18, e.g. `2014/level/draconic-1`; rules_get name \"Draconic\" shows them all.*")]
    public async Task CallTool_BothForALevelWithNoMatch_SaysWhereThatEditionsLevelsAreInsteadOfSearching(string argumentsJson, string side, string hint)
    {
        var text = await GetTextAsync(argumentsJson);

        var missing = side.StartsWith("## 2014", StringComparison.Ordinal)
            ? Between(text, side, "## 2024 (SRD 5.2.1)")
            : Between(text, side, null);
        Assert.Contains("\n" + hint, missing, StringComparison.Ordinal);
        Assert.DoesNotContain("rules_search", missing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"ref":"rules://attribution"}""")]
    [InlineData("""{"ref":"`rules://attribution`","edition":"2014"}""")]
    [InlineData("""{"name":"attribution"}""")]
    [InlineData("""{"name":"SRD Attribution"}""")]
    public async Task CallTool_Attribution_ReturnsTheAttributionResourceText(string argumentsJson)
    {
        // Claude Desktop attaches resources only by hand; the licence text must also be reachable through a tool.
        var resource = await _server.Client.ReadResourceAsync(DndMcp.Resources.RulesResources.AttributionUri);
        var expected = Assert.IsType<ModelContextProtocol.Protocol.TextResourceContents>(Assert.Single(resource.Contents)).Text;

        Assert.Equal(expected, await GetTextAsync(argumentsJson));
    }

    [Fact]
    public async Task CallTool_CorrectedRecord_SaysSoDirectlyUnderItsMetaLine()
    {
        // Corrected text must never pass as the untouched upstream record: the label is the only thing that says so.
        var lines = await GetLinesAsync("""{"name":"Potion of Heroism"}""");

        Assert.Equal("*magic-item · 2024 · SRD 5.2.1 · `2024/magic-item/potion-of-heroism`*", lines[1]);
        Assert.StartsWith("*Corrected from the upstream data: ", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("*", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BothWithACorrectedSide_LabelsThatSide()
    {
        var text = await GetTextAsync("""{"name":"Potion of Heroism","edition":"both"}""");

        Assert.Contains(
            "*magic-item · 2024 · SRD 5.2.1 · `2024/magic-item/potion-of-heroism`*\n*Corrected from the upstream data: ",
            Between(text, "## 2024 (SRD 5.2.1)", null),
            StringComparison.Ordinal);
    }

    private async Task<string> GetTextAsync(string argumentsJson) =>
        _server.SuccessText(await _server.CallToolJsonAsync(Tool, argumentsJson));

    private async Task<string> GetErrorAsync(string argumentsJson) =>
        _server.ErrorText(await _server.CallToolJsonAsync(Tool, argumentsJson));

    private async Task<string[]> GetLinesAsync(string argumentsJson) => (await GetTextAsync(argumentsJson)).Split('\n');

    // The first "*kind · edition · … · `ref`*" line of a comparison side.
    private static string FirstMetaLine(string side) =>
        side.Split('\n').FirstOrDefault(l => l.StartsWith('*') && l.Contains(" · `", StringComparison.Ordinal)) ?? string.Empty;

    private static string Between(string text, string start, string? end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"\"{start}\" not found in:\n{text}");
        var to = end is null ? text.Length : text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to >= 0, $"\"{end}\" not found after \"{start}\" in:\n{text}");
        return text[from..to];
    }
}
