using System.Text.Json;
using DndMcp.Domain.Campaign;
using Xunit;
using static DndMcp.Tests.Campaign.Knowers;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: a non-author view shows an entity only when its visibility is not author and the verdict knows it; an
/// entity known under a name that is not its own (or not recognised) is disguised, showing only that name and an
/// <c>e:&lt;seq&gt;</c> ref; and a <c>kind:slug</c> ref is printed to a non-author view only when the slug spells the name
/// that view already sees. These close the leaks through names and slugs that the FTS column filter cannot.
/// </summary>
public sealed class EntityViewsTests
{
    private static readonly (string Alias, string Visibility)[] OldKingAliases = [("the sorcerer king", "party"), ("Keras", "author")];

    private static readonly (string Alias, string Visibility)[] ThingAliases =
        [("the thing he wants", "party"), ("Axiom Cage", "author"), ("the Cage", "author")];

    [Fact]
    public void For_AuthorView_IsTheTrueNameAndSlug()
    {
        var view = EntityViews.For(Author(), "character", "The Old King", "party", OldKingAliases, Knows());

        // FD4: the view also carries the names it uses (every name and alias, for the author) and its alias audience.
        Assert.Equal(new EntityView(true, false, "The Old King", AuthorView: true) { SeesPartyAliases = true, UsedNames = ["old king", "sorcerer king", "keras"] },
            view);
        Assert.Equal("character:keras-2", EntityViews.Ref(view, "character", "keras-2", 12));
    }

    [Fact]
    public void For_AuthorVisibilityEvenWithAKnowsVerdict_IsHiddenFromEveryOtherView()
    {
        foreach (var perspective in new[] { "party", "table", "dm", "public" })
        {
            Assert.Equal(EntityView.Hidden, EntityViews.For(Of(perspective), "item", "The Axiom Cage", "author", [], Knows()));
        }
    }

    [Theory]
    [InlineData("NoRecord")]
    [InlineData("DoesNotKnow")]
    [InlineData("Uncertain")]
    public void For_VerdictThatDoesNotKnow_IsHidden(string standing)
    {
        var verdict = new KnowledgeVerdict(Enum.Parse<KnowledgeStanding>(standing), null, null, KnowledgeBases.None, null, "no record");

        var view = EntityViews.For(Of("party"), "character", "The Protector", "public", [], verdict);

        Assert.False(view.Visible);
        Assert.Equal(string.Empty, view.DisplayName);
    }

    [Fact]
    public void Ref_HiddenView_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => EntityViews.Ref(EntityView.Hidden, "character", "keras", 1));
    }

    [Theory]
    // Belmakor: the name the party uses is the entity's own name, with or without its article.
    [InlineData("The Old King", "the old king", "met", false, "The Old King")]
    [InlineData("The Old King", "Old King", "met", false, "The Old King")]
    [InlineData("The Old King", null, "met", false, "The Old King")]
    // Known only by one of its party aliases: still disguised, so an entity named "Keras" whose party alias is "the old
    // king" never prints "Keras" to the party (stage-1 finding 13).
    [InlineData("The Old King", "the sorcerer king", "met", true, "the sorcerer king")]
    [InlineData("The thing the old king wants", "the thing he wants", "aware", true, "the thing he wants")]
    [InlineData("Keras", "the old king", "met", true, "the old king")]
    // Known under an author alias or another name entirely: disguised.
    [InlineData("The Old King", "Keras", "knows", true, "Keras")]
    [InlineData("The Protector", "the advisor in Serret", "knows", true, "the advisor in Serret")]
    // Unrecognised: disguised even under the true name; with no name the kind stands in.
    [InlineData("The Protector", "the advisor in Serret", "unrecognized", true, "the advisor in Serret")]
    [InlineData("The Protector", "The Protector", "unrecognized", true, "The Protector")]
    [InlineData("The Protector", null, "unrecognized", true, "an unrecognized character")]
    public void For_KnownAs_DisguisesUnlessItIsTheEntitysOwnName(string name, string? knownAs, string state, bool disguised, string shown)
    {
        var aliases = name.Contains("thing", StringComparison.Ordinal) ? ThingAliases : OldKingAliases;
        var verdict = new KnowledgeVerdict(KnowledgeStanding.Knows, state, knownAs, KnowledgeBases.Party, 3, "x");

        var view = EntityViews.For(Of("party"), "character", name, "restricted", aliases, verdict);

        Assert.True(view.Visible);
        Assert.Equal(disguised, view.Disguised);
        Assert.Equal(shown, view.DisplayName);
        Assert.False(view.AuthorView);
    }

    [Theory]
    [InlineData("The Old King", "old-king", "character:old-king")]
    [InlineData("The Old King", "the-old-king", "character:the-old-king")]
    [InlineData("The Old King", "old-king-2", "e:7")]
    [InlineData("The Old King", "keras", "e:7")]
    [InlineData("Björn Mountainfell", "bjorn-mountainfell", "character:bjorn-mountainfell")]
    [InlineData("The Protector", "protector", "character:protector")]
    public void Ref_NonDisguisedEntity_PrintsKindSlugOnlyWhenTheSlugSpellsTheShownName(string name, string slug, string expected)
    {
        var view = EntityViews.For(Of("party"), "character", name, "party", [], Knows());

        Assert.Equal(expected, EntityViews.Ref(view, "character", slug, 7));
    }

    [Fact]
    public void Ref_DisguisedEntity_IsAlwaysTheSequenceHandle()
    {
        // The Protector's slug is "protector", but the party knows him only as "the advisor in Serret".
        var verdict = new KnowledgeVerdict(KnowledgeStanding.Knows, "unrecognized", "the advisor in Serret", KnowledgeBases.Explicit, 1, "x");
        var view = EntityViews.For(Of("party"), "character", "The Protector", "restricted", [], verdict);

        Assert.Equal("e:4", EntityViews.Ref(view, "character", "protector", 4));
    }

    [Fact]
    public void ShownAliases_PartyView_ShowsItsPublicAndPartyAliasesButOnlyThePartyOnesWhenDisguised()
    {
        (string Alias, string Visibility)[] aliases = [.. ThingAliases, ("the old relic", "public")];
        var view = EntityViews.For(Of("party"), "item", "The thing the old king wants", "party", aliases, Knows());
        var disguised = EntityViews.For(Of("party"), "item", "The thing the old king wants", "party", aliases, KnownAs("the box"));
        var byHand = new EntityView(true, true, "the box");

        Assert.Equal([("the thing he wants", "party"), ("the old relic", "public")], EntityViews.ShownAliases(view, aliases));
        // FD4 (review U01) changed the disguised case from none: a disguise keeps the party's own names. A public alias is
        // the world's name for what it really is, so a disguise still hides it (review of FD4).
        Assert.True(disguised.Disguised);
        Assert.Equal([("the thing he wants", "party")], EntityViews.ShownAliases(disguised, aliases));
        Assert.Empty(EntityViews.ShownAliases(byHand, aliases));
        Assert.Empty(EntityViews.ShownAliases(EntityView.Hidden, aliases));
    }

    [Fact]
    public void Ref_DisguisedEntityWhoseSlugSpellsItsDisplayName_IsStillTheSequenceHandle()
    {
        // Unrecognised under his true name: the slug spells exactly what is shown, yet a disguised entity never gets
        // kind:slug (the kind:slug form would confirm the name belongs to that entity).
        var verdict = new KnowledgeVerdict(KnowledgeStanding.Knows, "unrecognized", "The Protector", KnowledgeBases.Explicit, 1, "x");
        var view = EntityViews.For(Of("party"), "character", "The Protector", "restricted", [], verdict);

        Assert.True(view.Disguised);
        Assert.Equal("The Protector", view.DisplayName);
        Assert.Equal("e:4", EntityViews.Ref(view, "character", "protector", 4));
    }

    [Theory]
    [InlineData("party", "the thing he wants|the old relic")]
    [InlineData("table", "the thing he wants|the old relic")]
    [InlineData("dm", "the thing he wants|the old relic")]
    [InlineData("character", "the thing he wants|the old relic")]
    // FD3 (review L11) changed the public row from both: a party alias is for the views that see party rows.
    [InlineData("public", "the old relic")]
    public void ShownAliases_RestrictedAlias_IsNeverShownToANonAuthorView(string perspective, string expected)
    {
        (string Alias, string Visibility)[] aliases = [("the thing he wants", "party"), ("the old relic", "public"), ("the Cage", "restricted")];
        var who = perspective == "character" ? Character("c1", "Belmakor", new PartyMembership(null, null)) : Of(perspective);

        var view = EntityViews.For(who, "item", "The thing the old king wants", "public", aliases, Knows());
        var shown = EntityViews.ShownAliases(view, aliases);

        Assert.False(view.Disguised);
        Assert.Equal(expected.Split('|'), shown.Select(a => a.Alias));
        Assert.DoesNotContain(shown, a => a.Alias.Contains("Cage", StringComparison.Ordinal));
        Assert.DoesNotContain(view.UsedNames, n => n.Contains("cage", StringComparison.Ordinal));
    }

    /// <summary>
    /// FD3 (review L11): "The Masked Duke" is public, and the party alone knows him as Duke Orsino (a party alias). The
    /// public, a character who is not in the party and one who has left see only his public names; the party, the table,
    /// the dm and a current member see the party alias too.
    /// </summary>
    [Theory]
    [InlineData("public", false)]
    [InlineData("stranger", false)]
    [InlineData("former", false)]
    [InlineData("left", false)]
    [InlineData("member", true)]
    [InlineData("party", true)]
    [InlineData("table", true)]
    [InlineData("dm", true)]
    public void ShownAliases_PartyAlias_IsShownOnlyToViewsThatSeePartyRows(string perspective, bool seesOrsino)
    {
        (string Alias, string Visibility)[] aliases = [("Duke Orsino", "party"), ("the Masked One", "public"), ("the Gilded Fox", "author")];
        var who = perspective switch
        {
            "stranger" => Character("c9", "Old Tom", null),
            "former" => Character("c3", "Tristan", new PartyMembership(null, 1)),
            "left" => Character("c4", "Robin", new PartyMembership(null, null) { Former = true }),
            "member" => Character("c1", "Aria", new PartyMembership(null, null)),
            _ => Of(perspective),
        };

        var view = EntityViews.For(who, "character", "The Masked Duke", "public", aliases, Knows());
        var shown = EntityViews.ShownAliases(view, aliases).Select(a => a.Alias).ToList();

        Assert.Equal(seesOrsino ? ["Duke Orsino", "the Masked One"] : ["the Masked One"], shown);
        Assert.Equal(seesOrsino, view.UsedNames.Contains("duke orsino"));
        Assert.DoesNotContain("the Gilded Fox", shown);
    }

    [Fact]
    public void For_KnownAsARestrictedAlias_IsDisguisedAndRefIsTheSequenceHandle()
    {
        // A restricted alias is not one of the names the perspective already sees, so knowing the entity by it is a
        // disguise: the true name, summary and slug stay hidden.
        (string Alias, string Visibility)[] aliases = [("the Cage", "restricted"), ("the thing he wants", "party")];
        var verdict = new KnowledgeVerdict(KnowledgeStanding.Knows, "aware", "the Cage", KnowledgeBases.Party, 3, "x");

        var view = EntityViews.For(Of("party"), "item", "The Axiom Cage", "restricted", aliases, verdict);

        Assert.True(view.Disguised);
        Assert.Equal("the Cage", view.DisplayName);
        Assert.Equal("e:9", EntityViews.Ref(view, "item", "axiom-cage", 9));
        // FD4 (review U01) changed this from no aliases: the party alias is one the party may see; the restricted one is not.
        Assert.Equal([("the thing he wants", "party")], EntityViews.ShownAliases(view, aliases));
        Assert.Equal(["cage", "thing he wants"], view.UsedNames);
    }

    [Fact]
    public void ShownAliases_AuthorView_ShowsEveryAliasWithItsVisibility()
    {
        var view = EntityViews.For(Author(), "item", "The thing the old king wants", "party", ThingAliases, Knows());

        Assert.Equal(ThingAliases, EntityViews.ShownAliases(view, ThingAliases));
    }

    [Fact]
    public void For_DmInADmCampaign_IsTheAuthorView()
    {
        var view = EntityViews.For(Of("dm", "dm"), "character", "Keras", "author", [], Knows());

        Assert.True(view.AuthorView);
        Assert.Equal("Keras", view.DisplayName);
    }

    /// <summary>
    /// FD4 (review U01): the shape models write. "Keras" is the entity's name; the party knows him as "the ancient sorcerer
    /// king" (its row's known_as) and may see two party aliases. The party's view is disguised (no true name, summary or
    /// slug), shows the two party aliases, and uses all three names; nothing it carries says "Keras".
    /// </summary>
    [Theory]
    [InlineData("party")]
    [InlineData("member")]
    [InlineData("table")]
    public void For_DisguisedEntity_ShowsTheViewsOwnAliasesButNeverTheTrueName(string perspective)
    {
        (string Alias, string Visibility)[] aliases =
            [("the sorcerer king", "party"), ("the old king", "party"), ("Keras the Undying", "author"), ("Kerasorn", "restricted")];
        var who = perspective == "member" ? Character("c1", "Belmakor Silverwind", new PartyMembership(null, null)) : Of(perspective);

        var view = EntityViews.For(who, "character", "Keras", "party", aliases, KnownAs("the ancient sorcerer king"));
        var shown = EntityViews.ShownAliases(view, aliases);
        var reference = EntityViews.Ref(view, "character", "keras", 7);

        Assert.True(view.Disguised);
        Assert.Equal("the ancient sorcerer king", view.DisplayName);
        Assert.Equal([("the sorcerer king", "party"), ("the old king", "party")], shown);
        Assert.Equal(["ancient sorcerer king", "sorcerer king", "old king"], view.UsedNames);
        Assert.Equal("e:7", reference);
        var everything = JsonSerializer.Serialize(new { view, shown = shown.Select(a => a.Alias), reference });
        Assert.DoesNotContain("keras", everything, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A party alias that contains the entity's true name would undo the disguise, whatever else it adds and however it
    /// is spelled (stage-1 finding 13; the leak rule forbids the true name anywhere): it is neither shown nor a name the
    /// view uses. The scan is the name scanner's, so case, diacritics, an article and a possessive make no difference.
    /// </summary>
    [Theory]
    [InlineData("Keras")]
    [InlineData("the Keras")]
    [InlineData("King Keras")]
    [InlineData("Keras's ghost")]
    [InlineData("KERAS the Undying")]
    [InlineData("Kéras")]
    [InlineData("the tomb of Old King Keras")]
    public void For_DisguisedEntityWithAPartyAliasContainingItsTrueName_NeverShowsThatAlias(string alias)
    {
        (string Alias, string Visibility)[] aliases = [(alias, "party"), ("the old king", "party")];

        var view = EntityViews.For(Of("party"), "character", "Keras", "party", aliases, KnownAs("the ancient sorcerer king"));
        var shown = EntityViews.ShownAliases(view, aliases);

        Assert.Equal([("the old king", "party")], shown);
        Assert.Equal(["ancient sorcerer king", "old king"], view.UsedNames);
        var everything = JsonSerializer.Serialize(new { view, shown = shown.Select(a => a.Alias) });
        Assert.DoesNotContain("keras", everything, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Review of FD4: a disguise keeps only the party's own names. Morwen Vashkar is the Lich Queen to the world (a public
    /// alias) and "the crypt witch" to the party (a party alias); the party met her as "the veiled woman". A public alias
    /// beside a disguise unmasks her, so no disguised view shows it; a view that sees party aliases keeps the party's own
    /// name for her. One that does not recognise her (<c>unrecognized</c>) gets no alias at all: it does not know who she
    /// is. Undisguised, every view sees the aliases its audience admits, as before.
    /// </summary>
    [Theory]
    [InlineData("party", "met", "the veiled woman", "the crypt witch", "veiled woman|crypt witch")]
    [InlineData("member", "met", "the veiled woman", "the crypt witch", "veiled woman|crypt witch")]
    [InlineData("table", "met", "the veiled woman", "the crypt witch", "veiled woman|crypt witch")]
    [InlineData("dm", "met", "the veiled woman", "the crypt witch", "veiled woman|crypt witch")]
    [InlineData("public", "met", "the veiled woman", "", "veiled woman")]
    [InlineData("stranger", "met", "the veiled woman", "", "veiled woman")]
    [InlineData("former", "met", "the veiled woman", "", "veiled woman")]
    [InlineData("party", "unrecognized", "the veiled woman", "", "veiled woman")]
    [InlineData("member", "unrecognized", "the veiled woman", "", "veiled woman")]
    [InlineData("public", "unrecognized", "the veiled woman", "", "veiled woman")]
    [InlineData("party", "unrecognized", null, "", "")]
    [InlineData("table", "unrecognized", null, "", "")]
    [InlineData("party", "met", null, "the Lich Queen|the crypt witch", "morwen vashkar|lich queen|crypt witch")]
    [InlineData("public", "met", null, "the Lich Queen", "morwen vashkar|lich queen")]
    public void For_DisguisedEntity_SharesOnlyThePartysOwnAliasesAndNoneWhenUnrecognised(
        string perspective, string state, string? knownAs, string shown, string used)
    {
        (string Alias, string Visibility)[] aliases = [("the Lich Queen", "public"), ("the crypt witch", "party"), ("the Bone Mother", "author")];
        var who = perspective switch
        {
            "member" => Character("c1", "Aria", new PartyMembership(null, null)),
            "stranger" => Character("c9", "Old Tom", null),
            "former" => Character("c3", "Tristan", new PartyMembership(null, 1)),
            _ => Of(perspective),
        };
        var verdict = new KnowledgeVerdict(KnowledgeStanding.Knows, state, knownAs, KnowledgeBases.Party, 3, "x");

        var view = EntityViews.For(who, "character", "Morwen Vashkar", "party", aliases, verdict);
        var shownAliases = EntityViews.ShownAliases(view, aliases).Select(a => a.Alias).ToList();

        Assert.Equal(shown.Length == 0 ? [] : shown.Split('|'), shownAliases);
        Assert.Equal(used.Length == 0 ? [] : used.Split('|'), view.UsedNames);
        Assert.DoesNotContain("the Bone Mother", shownAliases);
        Assert.Equal(knownAs is not null || state == "unrecognized", view.Disguised);
        if (view.Disguised)
        {
            var everything = JsonSerializer.Serialize(new { view, shownAliases });
            Assert.DoesNotContain("Lich", everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Morwen", everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A public alias is never printed under a disguise, even one spelled like a party alias the view shares (the alias
    /// key ignores case and the article, so "Crypt Witch" and "the crypt witch" can both be stored): the rule is by
    /// visibility, not by spelling.
    /// </summary>
    [Fact]
    public void ShownAliases_DisguisedView_NeverPrintsAPublicAliasEvenOneSpelledLikeASharedPartyAlias()
    {
        (string Alias, string Visibility)[] aliases = [("the crypt witch", "party"), ("Crypt Witch", "public")];

        var view = EntityViews.For(Of("party"), "character", "Morwen Vashkar", "party", aliases, KnownAs("the veiled woman"));

        Assert.Equal([("the crypt witch", "party")], EntityViews.ShownAliases(view, aliases));
    }

    /// <summary>
    /// A disguised view whose known_as is also one of its party aliases (the shape a model writes: the party's name for
    /// him recorded both ways) uses that name once and does not list it again among the aliases under it.
    /// </summary>
    [Fact]
    public void For_DisguisedEntityKnownByOneOfItsPartyAliases_UsesThatNameOnceAndDoesNotRepeatIt()
    {
        (string Alias, string Visibility)[] aliases = [("the ancient sorcerer king", "party"), ("the old king", "party")];

        var view = EntityViews.For(Of("party"), "character", "Keras", "party", aliases, KnownAs("The Ancient Sorcerer King"));

        Assert.Equal(["ancient sorcerer king", "old king"], view.UsedNames);
        Assert.Equal([("the old king", "party")], EntityViews.ShownAliases(view, aliases));
    }

    /// <summary>
    /// FD4: the names a view uses for an entity, as keys without articles, for the read path's search and check: the name
    /// it is shown plus the aliases it may print. The "an unrecognized character" stand-in is nobody's name, an entity the
    /// view does not recognise shares no alias, and a hidden entity has no names.
    /// </summary>
    [Theory]
    [InlineData("party", "The Old King", null, "met", "old king|sorcerer king")]
    [InlineData("public", "The Old King", null, "met", "old king")]
    [InlineData("party", "The Old King", "the bearded stranger", "met", "bearded stranger|sorcerer king")]
    [InlineData("public", "The Old King", "the bearded stranger", "met", "bearded stranger")]
    // Review of FD4 changed this row from "sorcerer king": an entity the view does not recognise shares no alias.
    [InlineData("party", "The Old King", null, "unrecognized", "")]
    [InlineData("party", "The Old King", "the bearded stranger", "unrecognized", "bearded stranger")]
    [InlineData("public", "The Old King", null, "unrecognized", "")]
    [InlineData("party", "The Old King", "Old King", "met", "old king|sorcerer king")]
    public void UsedNames_View_IsTheShownNameAndTheAliasesItMaySee(string perspective, string name, string? knownAs, string state, string expected)
    {
        var verdict = new KnowledgeVerdict(KnowledgeStanding.Knows, state, knownAs, KnowledgeBases.Explicit, 2, "x");

        var view = EntityViews.For(Of(perspective), "character", name, "public", OldKingAliases, verdict);

        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), view.UsedNames);
        Assert.DoesNotContain("keras", view.UsedNames);
        Assert.Empty(EntityView.Hidden.UsedNames);
    }

    [Fact]
    public void Equals_TwoViewsOfTheSameEntity_AreEqualByValue()
    {
        var first = EntityViews.For(Of("party"), "character", "The Old King", "party", OldKingAliases, Knows());
        var second = EntityViews.For(Of("party"), "character", "The Old King", "party", OldKingAliases, Knows());
        var publicView = EntityViews.For(Of("public"), "character", "The Old King", "public", OldKingAliases, Knows());

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, publicView);
    }

    /// <summary>
    /// <see cref="EntityView.UsedNames"/> takes part in equality item by item (a record would compare the list by
    /// reference), and so does <see cref="EntityView.SeesPartyAliases"/>: two views that differ in only one of them are
    /// different views, and equal lists in different instances are the same view.
    /// </summary>
    [Fact]
    public void Equals_ViewsThatDifferOnlyInUsedNamesOrAliasAudience_AreNotEqual()
    {
        var view = new EntityView(true, true, "the box") { SeesPartyAliases = true, UsedNames = ["box", "thing he wants"] };
        var sameNames = new EntityView(true, true, "the box") { SeesPartyAliases = true, UsedNames = new List<string> { "box", "thing he wants" } };
        var otherNames = view with { UsedNames = ["box"] };
        var otherAudience = view with { SeesPartyAliases = false };

        Assert.Equal(view, sameNames);
        Assert.Equal(view.GetHashCode(), sameNames.GetHashCode());
        Assert.NotEqual(view, otherNames);
        Assert.NotEqual(view, otherAudience);
    }

    private static KnowledgeVerdict Knows() => new(KnowledgeStanding.Knows, "knows", null, KnowledgeBases.Visibility, null, "x");

    private static KnowledgeVerdict KnownAs(string name) => new(KnowledgeStanding.Knows, "met", name, KnowledgeBases.Party, 3, "x");
}
