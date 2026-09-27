using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: a rule, condition, damage type or magic school body is the SRD text complete and in reading order, with
/// the links a model needs to move around the rules: where a 2014 rule sits and what is under it, and which glossary
/// entries a 2024 rule points to, each as a ref that fetches. 2014 rule text is upstream markdown and is shown
/// byte-for-byte; every other text keeps its words and gains only paragraph breaks (<see cref="SrdProse"/>).
///
/// <para>
/// Expected text was checked by hand against the vendored JSON and the glossary.
/// </para>
/// </summary>
public sealed class RuleMarkdownTests
{
    private static readonly string[] OwnedKinds = [SrdKinds.Rule, SrdKinds.Condition, SrdKinds.DamageType, SrdKinds.MagicSchool];

    // A heading-only chapter has no text of its own; its body is the chapter's table of contents, two levels deep.
    [Fact]
    public void Body_2014HeadingOnlyChapter_IsItsNestedTableOfContents()
    {
        var lines = Lines(Body("2014/rule/combat"));

        Assert.Equal(
            new[]
            {
                "### Subsections",
                "",
                "- The Order of Combat (`2014/rule/the-order-of-combat`)",
                "  - Combat Step by Step (`2014/rule/combat-step-by-step`)",
                "  - Surprise (`2014/rule/surprise`)",
            },
            lines.Take(5));
        Assert.Contains("- Cover (`2014/rule/cover`)", lines);
        Assert.Equal("- Underwater Combat (`2014/rule/underwater-combat`)", lines[^1]);
        Assert.Equal(8, lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
        Assert.Equal(35, lines.Count(l => l.StartsWith("  - ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Body_2014ChapterThreeLevelsDeep_NestsEachLevelTwoSpacesFurther()
    {
        Assert.Equal(
            "### Subsections\n\n" +
            "- Fantasy-Historical Pantheons (`2014/rule/fantasy-historical-pantheons`)\n" +
            "  - The Celtic Pantheon (`2014/rule/the-celtic-pantheon`)\n" +
            "  - The Greek Pantheon (`2014/rule/the-greek-pantheon`)\n" +
            "  - The Egyptian Pantheon (`2014/rule/the-egyptian-pantheon`)\n" +
            "  - The Norse Pantheon (`2014/rule/the-norse-pantheon`)\n" +
            "- The Planes of Existence (`2014/rule/the-planes-of-existence`)\n" +
            "  - The Material Plane (`2014/rule/the-material-plane`)\n" +
            "  - Beyond the Material (`2014/rule/beyond-the-material`)\n" +
            "    - Planar Travel (`2014/rule/planar-travel`)\n" +
            "    - Transitive Planes (`2014/rule/transitive-planes`)\n" +
            "    - Inner Planes (`2014/rule/inner-planes`)\n" +
            "    - Outer Planes (`2014/rule/outer-planes`)",
            Body("2014/rule/appendix"));
    }

    [Theory]
    [InlineData("2014/rule/cover", "**Part of** Combat (`2014/rule/combat`)")]
    [InlineData("2014/rule/actions-in-combat", "**Part of** Combat (`2014/rule/combat`)")]
    [InlineData("2014/rule/combat-step-by-step",
        "**Part of** Combat (`2014/rule/combat`) › The Order of Combat (`2014/rule/the-order-of-combat`)")]
    [InlineData("2014/rule/planar-travel",
        "**Part of** Appendix (`2014/rule/appendix`) › The Planes of Existence (`2014/rule/the-planes-of-existence`) › Beyond the Material (`2014/rule/beyond-the-material`)")]
    public void Body_2014NestedRule_StartsWithItsBreadcrumbOutermostFirst(string reference, string expectedFirstLine)
    {
        Assert.Equal(expectedFirstLine, Lines(Body(reference))[0]);
    }

    [Theory]
    [InlineData("2014/rule/combat")]
    [InlineData("2014/rule/using-ability-scores")]
    [InlineData("2014/rule/spellcasting")]
    public void Body_2014TopLevelRule_HasNoBreadcrumb(string reference)
    {
        Assert.DoesNotContain("**Part of**", Body(reference), StringComparison.Ordinal);
    }

    [Fact]
    public void Body_2014RuleWithTextAndChildren_IsBreadcrumbTextThenSubsections()
    {
        Assert.Equal(
            "**Part of** Combat (`2014/rule/combat`)\n\n" +
            "When you take your action on your turn, you can take one of the actions presented here, an action you gained from your class or a special feature, or an action that you improvise. Many monsters have action options of their own in their stat blocks.\n\n" +
            "When you describe an action not detailed elsewhere in the rules, the GM tells you whether that action is possible and what kind of roll you need to make, if any, to determine success or failure.\n\n" +
            "### Subsections\n\n" +
            "- Attack (`2014/rule/attack`)\n- Cast a Spell (`2014/rule/cast-a-spell`)\n- Dash (`2014/rule/dash`)\n" +
            "- Disengage (`2014/rule/disengage`)\n- Dodge (`2014/rule/dodge`)\n- Help (`2014/rule/help`)\n" +
            "- Hide (`2014/rule/hide`)\n- Ready (`2014/rule/ready`)\n- Search (`2014/rule/search`)\n" +
            "- Use an Object (`2014/rule/use-an-object`)",
            Body("2014/rule/actions-in-combat"));
    }

    [Fact]
    public void Body_2014LeafRule_HasNoSubsectionsHeading()
    {
        var body = Body("2014/rule/cover");

        Assert.DoesNotContain("### Subsections", body, StringComparison.Ordinal);
        Assert.Contains("A target with **half cover** has a +2 bonus to AC and Dexterity saving throws.", body, StringComparison.Ordinal);
        Assert.EndsWith("A target has total cover if it is completely concealed by an obstacle.", body, StringComparison.Ordinal);
    }

    // Upstream markdown is shown as it is: a re-flow would, for one, split "your Strength\n(Athletics) check" (a soft
    // line break inside melee-attacks' Escaping a Grapple) into two paragraphs.
    [Fact]
    public void Body_Every2014RuleWithText_ContainsThatTextVerbatim()
    {
        var withText = 0;
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(SrdEdition.Edition2014, SrdKinds.Rule))
        {
            if (doc.Root.TryGetProperty("desc", out var desc))
            {
                Assert.Contains(desc.GetString()!.Trim(), Body(doc), StringComparison.Ordinal);
                withText++;
            }
        }

        Assert.Equal(134, withText);
    }

    [Fact]
    public void Body_Every2014Rule_ListsEachChildAsAResolvableRef()
    {
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(SrdEdition.Edition2014, SrdKinds.Rule))
        {
            var lines = Lines(Body(doc));
            var children = doc.Root.TryGetProperty("children", out var array) ? array.EnumerateArray().ToList() : [];

            Assert.Equal(children.Count > 0, lines.Contains("### Subsections"));
            foreach (var child in children)
            {
                var slug = child.GetProperty("index").GetString()!;
                Assert.NotNull(VendoredSrdLookup.Instance.Get(SrdEdition.Edition2014, SrdKinds.Rule, slug));
                Assert.Contains($"- {child.GetProperty("name").GetString()} (`2014/rule/{slug}`)", lines);
            }
        }
    }

    // The longest rule (about 10,000 characters of traps) renders whole, with upstream's #### headings kept at ####.
    [Fact]
    public void Format_2014SampleTraps_RendersWholeAndKeepsItsHeadings()
    {
        var doc = VendoredSrdLookup.Instance.Require("2014/rule/sample-traps");

        var text = SrdMarkdown.Format(doc, SrdMarkdown.Concise, VendoredSrdLookup.Instance);

        Assert.True(text.Length > 10_000, $"Only {text.Length} characters.");
        Assert.DoesNotContain("[Truncated", text, StringComparison.Ordinal);
        Assert.Contains("\n#### Collapsing Roof\n\n*Mechanical trap*\n", text, StringComparison.Ordinal);
        Assert.Contains("\n#### Sphere of Annihilation\n", text, StringComparison.Ordinal);
        Assert.EndsWith("A successful *dispel magic* (DC 18) removes this enchantment.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_2024GlossaryEntryWithCategory_IsCategoryThenText()
    {
        Assert.Equal(
            "*Weapon Property*\n\n" +
            "When making an attack with a Finesse weapon, use your choice of your Strength or Dexterity modifier for the attack and damage rolls. You must use the same modifier for both rolls.",
            Body("2024/rule/finesse-weapon-property"));
    }

    [Theory]
    [InlineData("2024/rule/grappling", "**Related** Unarmed Strike (`2024/rule/unarmed-strike`), Grappled (`2024/rule/grappled`)")]
    [InlineData("2024/rule/climb-speed", "**Related** Climbing (`2024/rule/climbing`), Speed (`2024/rule/speed`)")]
    [InlineData("2024/rule/reach-weapon-property", "**Related** Reach (`2024/rule/reach`)")]
    [InlineData("2024/rule/long-rest", "**Related** Short Rest (`2024/rule/short-rest`)")]
    public void Body_2024GlossaryEntry_EndsWithRelatedEntriesAsRefs(string reference, string expectedLastLine)
    {
        Assert.Equal(expectedLastLine, Lines(Body(reference))[^1]);
    }

    // All 78 related names in the glossary resolve; each must be shown with the ref that fetches it.
    [Fact]
    public void Body_Every2024GlossaryRelatedName_IsShownWithItsRef()
    {
        var related = 0;
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(SrdEdition.Edition2024, SrdKinds.Rule))
        {
            if (!doc.Root.TryGetProperty("related", out var names))
            {
                Assert.DoesNotContain("**Related**", Body(doc), StringComparison.Ordinal);
                continue;
            }

            var line = Lines(Body(doc))[^1];
            foreach (var name in names.EnumerateArray().Select(n => n.GetString()!))
            {
                Assert.Contains($"{name} (`2024/rule/{SrdSlug.FromName(name)}`)", line, StringComparison.Ordinal);
                related++;
            }
        }

        Assert.Equal(78, related);
    }

    [Fact]
    public void Body_2024GlossaryRelatedNameThatResolvesToNothing_IsShownPlain()
    {
        var doc = Glossary("""{"name":"Test","description":"Text.","tags":[],"related":["No Such Entry","Grappled"]}""");

        Assert.Equal("Text.\n\n**Related** No Such Entry, Grappled (`2024/rule/grappled`)", SrdMarkdown.Body(doc, VendoredSrdLookup.Instance));
    }

    // The glossary starts tables right after a text line and resumes text right after the last row; in markdown the text
    // line after a table would become another row.
    [Theory]
    [InlineData("2024/rule/breaking-objects",
        "**_Armor Class._** The Object Armor Class table suggests ACs for various substances.\n\nTable: Object Armor Class\n\n| AC | Substance |\n|----|---------------------|\n| 11 | Cloth, paper, rope |")]
    [InlineData("2024/rule/breaking-objects", "| 23 | Adamantine |\n\n**_Hit Points._** An object is destroyed")]
    [InlineData("2024/rule/action",
        "These actions are defined elsewhere in this glossary:\n\n| | | | |\n|-----------|-------|-----------|---------|\n| Attack | Dodge | Influence | Search |")]
    [InlineData("2024/rule/long-rest",
        "A Long Rest is stopped by the following interruptions:\n\n- Rolling Initiative\n- Casting a spell other than a cantrip\n- Taking any damage\n- 1 hour of walking or other physical exertion\n\nIf you rested at least 1 hour")]
    [InlineData("2024/rule/long-rest",
        "or standing watch.\n\nDuring sleep, you have the Unconscious condition.")]
    public void Body_2024GlossaryText_SeparatesParagraphsTablesAndLists(string reference, string expectedFragment)
    {
        Assert.Contains(expectedFragment, Body(reference), StringComparison.Ordinal);
    }

    // In the glossary every category equals the entry's one tag, so each source must be pinned on its own.
    [Theory]
    [InlineData("""{"name":"T","description":"Text.","category":"Hazard","tags":[]}""", "*Hazard*\n\nText.")]
    [InlineData("""{"name":"T","description":"Text.","tags":["Hazard"]}""", "*Hazard*\n\nText.")]
    [InlineData("""{"name":"T","description":"Text.","category":"Condition","tags":["condition","Extra"]}""", "*Condition, Extra*\n\nText.")]
    public void Body_2024GlossaryLabels_ComeFromCategoryAndTagsWithoutRepeats(string json, string expectedBody)
    {
        Assert.Equal(expectedBody, SrdMarkdown.Body(Glossary(json), VendoredSrdLookup.Instance));
    }

    // 50 glossary entries point at SRD 5.2.1 chapters ("See also 'Playing the Game' ('Damage and Healing')") whose text
    // is not served; without a word about it a model spends calls searching for them. The note follows the text, so the
    // SRD's own words stay as they are.
    [Theory]
    [InlineData("2024/rule/critical-hit",
        "Then add any relevant modifiers. *See also* \"Playing the Game\" (\"Damage and Healing\").",
        "*Not included here: the text of the SRD 5.2.1 chapter \"Playing the Game\". The 2024 rules available are this Rules Glossary and the individual entries (spells, equipment, classes, monsters and more).*")]
    [InlineData("2024/rule/spell",
        "A spell is a magical effect that has the characteristics described in \"Spells.\"",
        "*Not included here: the text of the SRD 5.2.1 chapter \"Spells\". The 2024 rules available are this Rules Glossary and the individual entries (spells, equipment, classes, monsters and more).*")]
    // "Magic Items" in parentheses is a section of the Equipment chapter, not a chapter of its own.
    [InlineData("2024/rule/attunement",
        "*See also* \"Equipment\" (\"Magic Items\").",
        "*Not included here: the text of the SRD 5.2.1 chapter \"Equipment\". The 2024 rules available are this Rules Glossary and the individual entries (spells, equipment, classes, monsters and more).*")]
    // Chapters in the order the text cites them.
    [InlineData("2024/rule/stat-block",
        "you don't use both.",
        "*Not included here: the text of the SRD 5.2.1 chapters \"Playing the Game\" and \"Monsters\". The 2024 rules available are this Rules Glossary and the individual entries (spells, equipment, classes, monsters and more).*")]
    public void Body_2024GlossaryEntryCitingAnSrdChapter_SaysAfterTheTextThatTheChapterIsNotIncluded(
        string reference, string textEnd, string expectedNote)
    {
        var body = Body(reference);

        Assert.Contains(textEnd + "\n\n" + expectedNote, body, StringComparison.Ordinal);
    }

    // Glossary names in quotes ("Speed.", "Grappled.") are entries this server has; only chapter titles get the note.
    [Theory]
    [InlineData("2024/rule/long-jump")]
    [InlineData("2024/rule/grappling")]
    [InlineData("2024/rule/climb-speed")]
    [InlineData("2024/rule/finesse-weapon-property")]
    public void Body_2024GlossaryEntryCitingNoChapter_HasNoChapterNote(string reference)
    {
        Assert.DoesNotContain("Not included here", Body(reference), StringComparison.Ordinal);
    }

    [Fact]
    public void Body_Every2024GlossaryEntry_NotesAChapterOnlyWhenItsTextQuotesOne()
    {
        string[] chapters =
        [
            "Playing the Game", "Character Creation", "Classes", "Character Origins", "Feats", "Equipment", "Spells",
            "Gameplay Toolbox", "Magic Items", "Monsters", "Monsters A–Z", "Animals",
        ];

        var noted = 0;
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(SrdEdition.Edition2024, SrdKinds.Rule))
        {
            var text = doc.Root.GetProperty("description").GetString()!;
            var body = Body(doc);
            var note = Lines(body).SingleOrDefault(l => l.StartsWith("*Not included here:", StringComparison.Ordinal));
            if (note is null)
            {
                continue;
            }

            noted++;
            foreach (var chapter in Regex.Matches(note, "\"([^\"]+)\"").Select(m => m.Groups[1].Value))
            {
                Assert.Contains(chapter, chapters);
                Assert.Contains("\"" + chapter, text, StringComparison.Ordinal);
            }
        }

        Assert.Equal(50, noted);
    }

    // Chapters are named in the order the text cites them, joined the way the SRD writes a list.
    [Theory]
    [InlineData("See \"Spells\" and \"Playing the Game.\"", "chapters \"Spells\" and \"Playing the Game\"")]
    [InlineData("See \"Spells\", \"Equipment\" and \"Playing the Game.\" Also \"Spells.\"",
        "chapters \"Spells\", \"Equipment\" and \"Playing the Game\"")]
    [InlineData("Details are in \u201cMonsters A\u2013Z.\u201d", "chapter \"Monsters A\u2013Z\"")]
    public void Body_2024GlossaryEntryCitingSeveralChapters_NamesEachOnceInTheOrderCited(string text, string expectedChapters)
    {
        var doc = Glossary(JsonSerializer.Serialize(new { name = "Test", description = text, tags = Array.Empty<string>() }));

        Assert.Equal(
            text + "\n\n*Not included here: the text of the SRD 5.2.1 " + expectedChapters +
            ". The 2024 rules available are this Rules Glossary and the individual entries (spells, equipment, classes, monsters and more).*",
            SrdMarkdown.Body(doc, VendoredSrdLookup.Instance));
    }

    // A chapter title that is also a glossary entry's name is a link the model can follow, not a missing chapter.
    [Fact]
    public void Body_2024GlossaryEntryQuotingATitleTheGlossaryHas_HasNoChapterNote()
    {
        var citing = Glossary("""{"name":"Test","description":"See also \"Equipment.\"","tags":[]}""");
        var lookup = new RuleTreeLookup(
            citing,
            new SrdDocument { Edition = SrdEdition.Edition2024, Kind = SrdKinds.Rule, Slug = "equipment", Name = "Equipment", Json = """{"name":"Equipment","description":"Gear."}""" });

        Assert.Equal("See also \"Equipment.\"", SrdMarkdown.Body(citing, lookup));
    }

    [Fact]
    public void Body_2024GlossaryEntryWithoutCategory_StartsWithItsText()
    {
        Assert.StartsWith("A creature can grapple another creature.", Body("2024/rule/grappling"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2014/condition/grappled",
        "- A grappled creature's speed becomes 0, and it can't benefit from any bonus to its speed.\n" +
        "- The condition ends if the grappler is incapacitated (see the condition).\n" +
        "- The condition also ends if an effect removes the grappled creature from the reach of the grappler or grappling effect, such as when a creature is hurled away by the thunderwave spell.")]
    [InlineData("2014/condition/prone",
        "- A prone creature's only movement option is to crawl, unless it stands up and thereby ends the condition.\n" +
        "- The creature has disadvantage on attack rolls.\n" +
        "- An attack roll against the creature has advantage if the attacker is within 5 feet of the creature. Otherwise, the attack roll has disadvantage.")]
    [InlineData("2024/condition/grappled",
        "While you have the Grappled condition, you experience the following effects.\n\n" +
        "**Speed 0.** Your Speed is 0 and can't increase.\n\n" +
        "**Attacks Affected.** You have Disadvantage on attack rolls against any target other than the grappler.\n\n" +
        "**Movable.** The grappler can drag or carry you when it moves, but every foot of movement costs it 1 extra foot unless you are Tiny or two or more sizes smaller.")]
    [InlineData("2024/condition/prone",
        "While you have the Prone condition, you experience the following effects.\n\n" +
        "**Restricted Movement.** Your only movement options are to crawl or to spend an amount of movement equal to half your Speed (round down) to right yourself and thereby end the condition. If your Speed is 0, you can't right yourself.\n\n" +
        "**Attacks Affected.** You have Disadvantage on attack rolls. An attack roll against you has Advantage if the attacker is within 5 feet of you. Otherwise, that attack roll has Disadvantage.")]
    // The same conditions as 2024 Rules Glossary entries, which is what rules_get serves for kind "rule".
    [InlineData("2024/rule/grappled",
        "*Condition*\n\n" +
        "While you have the Grappled condition, you experience the following effects.\n\n" +
        "**_Speed 0._** Your Speed is 0 and can't increase.\n\n" +
        "**_Attacks Affected._** You have Disadvantage on attack rolls against any target other than the grappler.\n\n" +
        "**_Movable._** The grappler can drag or carry you when it moves, but every foot of movement costs it 1 extra foot unless you are Tiny or two or more sizes smaller than it.")]
    [InlineData("2024/rule/prone",
        "*Condition*\n\n" +
        "While you have the Prone condition, you experience the following effects.\n\n" +
        "**_Restricted Movement._** Your only movement options are to crawl or to spend an amount of movement equal to half your Speed (round down) to right yourself and thereby end the condition. If your Speed is 0, you can't right yourself.\n\n" +
        "**_Attacks Affected._** You have Disadvantage on attack rolls. An attack roll against you has Advantage if the attacker is within 5 feet of you. Otherwise, that attack roll has Disadvantage.")]
    [InlineData("2014/damage-type/fire", "Red dragons breathe fire, and many spells conjure flames to deal fire damage.")]
    [InlineData("2024/damage-type/fire", "Flames, unbearable heat")]
    [InlineData("2014/magic-school/evocation",
        "Evocation spells manipulate magical energy to produce a desired effect. Some call up blasts of fire or lightning. Others channel positive energy to heal wounds.")]
    [InlineData("2024/magic-school/evocation", "Channels energy to create effects that are often destructive")]
    public void Body_ConditionDamageTypeAndSchool_IsTheSrdTextWithParagraphsAndListsIntact(string reference, string expectedBody)
    {
        Assert.Equal(expectedBody, Body(reference));
    }

    // 2014 exhaustion's level lines ("1 - Disadvantage on ability checks") are not markdown list items; each stays its
    // own paragraph rather than being glued into one run-on line.
    [Fact]
    public void Body_2014Exhaustion_KeepsEachLevelLineAsItsOwnParagraph()
    {
        Assert.Contains(
            "as specified in the effect's description.\n\n1 - Disadvantage on ability checks\n\n2 - Speed halved\n\n3 - Disadvantage on attack rolls and saving throws",
            Body("2014/condition/exhaustion"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(OwnedEditionKinds))]
    public void Body_EveryDocumentOfOwnedKind_KeepsTablesWhole(string edition, string kind)
    {
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(edition, kind))
        {
            Assert.False(Regex.IsMatch(Body(doc), @"\|[ \t]*\n[ \t]*\n[ \t]*\|"), $"{doc.Ref}: a table is split by a blank line.");
        }
    }

    public static TheoryData<string, string> OwnedEditionKinds()
    {
        var data = new TheoryData<string, string>();
        foreach (var edition in SrdEdition.All)
        {
            foreach (var kind in OwnedKinds.Where(k => SrdKinds.ExistsIn(k, edition)))
            {
                data.Add(edition, kind);
            }
        }

        return data;
    }

    // A parent or child the index does not have is named without a ref (a ref that fetches nothing sends the model in a
    // circle), and the walk stops there.
    [Fact]
    public void Body_2014RuleLinkingToUnknownRules_NamesThemWithoutRefs()
    {
        var doc = new SrdDocument
        {
            Edition = SrdEdition.Edition2014,
            Kind = SrdKinds.Rule,
            Slug = "orphan",
            Name = "Orphan",
            Json = """
                   {"index":"orphan","name":"Orphan","desc":"Text.",
                    "parent":{"index":"nowhere","name":"Nowhere","url":"/api/2014/rules/nowhere"},
                    "children":[{"index":"gone","name":"Gone","url":"/api/2014/rules/gone"},{"index":"cover","name":"Cover","url":"/api/2014/rules/cover"}]}
                   """,
        };

        Assert.Equal(
            "**Part of** Nowhere\n\nText.\n\n### Subsections\n\n- Gone\n- Cover (`2014/rule/cover`)",
            SrdMarkdown.Body(doc, VendoredSrdLookup.Instance));
    }

    // A re-vendor that introduced a cycle must not hang rules_get: each node is walked at most once.
    [Fact]
    public void Body_2014RuleTreeWithACycle_TerminatesAndVisitsEachNodeOnce()
    {
        var lookup = new RuleTreeLookup(
            Rule("a", "A", parent: "b", children: ["b"]),
            Rule("b", "B", parent: "a", children: ["a"]));

        var body = SrdMarkdown.Body(lookup.Get(SrdEdition.Edition2014, SrdKinds.Rule, "a")!, lookup);

        Assert.Equal("**Part of** B (`2014/rule/b`)\n\nText of A.\n\n### Subsections\n\n- B (`2014/rule/b`)\n  - A (`2014/rule/a`)", body);
    }

    [Theory]
    [InlineData(SrdEdition.Edition2014, SrdKinds.Rule, """{"index":"empty","name":"Empty"}""")]
    [InlineData(SrdEdition.Edition2014, SrdKinds.Rule, """{"desc":7,"parent":"combat","children":{"a":1}}""")]
    [InlineData(SrdEdition.Edition2024, SrdKinds.Rule, """{"name":"Empty","description":7,"tags":"Condition","related":"Grappled"}""")]
    [InlineData(SrdEdition.Edition2014, SrdKinds.Condition, """{"desc":[" ",""]}""")]
    [InlineData(SrdEdition.Edition2024, SrdKinds.MagicSchool, """{}""")]
    public void Body_RecordWithNothingToShow_SaysSoWithoutThrowing(string edition, string kind, string json)
    {
        var doc = new SrdDocument { Edition = edition, Kind = kind, Slug = "empty", Name = "Empty", Json = json };

        Assert.Equal(SrdMarkdownText.NoDescription, SrdMarkdown.Body(doc, VendoredSrdLookup.Instance));
    }

    private static string Body(string reference) => Body(VendoredSrdLookup.Instance.Require(reference));

    private static string Body(SrdDocument doc) => SrdMarkdown.Body(doc, VendoredSrdLookup.Instance);

    private static string[] Lines(string body) => body.Split('\n');

    private static SrdDocument Glossary(string json) =>
        new() { Edition = SrdEdition.Edition2024, Kind = SrdKinds.Rule, Slug = "test", Name = "Test", Json = json };

    private static SrdDocument Rule(string slug, string name, string parent, string[] children)
    {
        var childJson = string.Join(",", children.Select(c => $$"""{"index":"{{c}}","name":"{{c.ToUpperInvariant()}}","url":"/api/2014/rules/{{c}}"}"""));
        return new SrdDocument
        {
            Edition = SrdEdition.Edition2014,
            Kind = SrdKinds.Rule,
            Slug = slug,
            Name = name,
            Json = $$"""
                     {"index":"{{slug}}","name":"{{name}}","desc":"Text of {{name}}.",
                      "parent":{"index":"{{parent}}","name":"{{parent.ToUpperInvariant()}}","url":"/api/2014/rules/{{parent}}"},
                      "children":[{{childJson}}]}
                     """,
        };
    }

    /// <summary>Hand-built rules, for shapes the vendored data does not have (a cycle, a glossary entry named like a chapter).</summary>
    private sealed class RuleTreeLookup : ISrdLookup
    {
        private readonly Dictionary<(string Edition, string Slug), SrdDocument> _bySlug;

        public RuleTreeLookup(params SrdDocument[] docs)
        {
            _bySlug = docs.ToDictionary(d => (d.Edition, d.Slug));
        }

        public SrdDocument? Get(string edition, string kind, string slug) =>
            kind == SrdKinds.Rule ? _bySlug.GetValueOrDefault((edition, slug)) : null;

        public IReadOnlyList<SrdDocument> ClassLevels(string edition, string classSlug) => [];

        public IReadOnlyList<SrdDocument> SubclassLevels(string edition, string subclassSlug) => [];
    }
}
