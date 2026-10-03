using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: a knower is one of the perspective vocabulary (party, table, public, dm, author, character:&lt;handle&gt;),
/// matched forgivingly; a knower list refuses a knower named twice (one row per knower per target) and reports every
/// field problem by position; attendance lists refuse duplicates and empty lists, which would change what the verdicts
/// read.
/// </summary>
public sealed class KnowerSpecsTests
{
    [Theory]
    [InlineData("party", "party", null)]
    [InlineData(" Party ", "party", null)]
    [InlineData("TABLE", "table", null)]
    [InlineData("dm", "dm", null)]
    [InlineData("author", "author", null)]
    [InlineData("public", "public", null)]
    [InlineData("character:belmakor", "character", "belmakor")]
    [InlineData("Character: Belmakor Silverwind", "character", "belmakor-silverwind")]
    [InlineData("character:character:belmakor", "character", "belmakor")]
    [InlineData("character:e:12", "character", "e:12")]
    public void Parse_Knower_GivesTheKindAndCharacterHandle(string who, string kind, string? character)
    {
        var (knowerKind, handle) = KnowerSpecs.Parse(who);

        Assert.Equal(kind, knowerKind);
        Assert.Equal(character, handle?.Text);
    }

    [Theory]
    [InlineData(null, "who is required")]
    [InlineData("everyone", "who \"everyone\" is not a knower")]
    [InlineData("party:ignis", "only character:<handle> takes a handle")]
    [InlineData("character", "needs the character after a colon")]
    [InlineData("character:f:2", "must name a character")]
    [InlineData("character:item:the-cage", "a knower is a character, not kind item")]
    public void Parse_NotAKnower_IsRefused(string? who, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => KnowerSpecs.Parse(who));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GoodList_HasNoProblems()
    {
        KnowerSpec?[] knowers =
        [
            new KnowerSpec { Who = "party", State = "met", KnownAs = "the old king", How = "witnessed", Session = 3 },
            new KnowerSpec { Who = "character:belmakor", State = "aware", Via = "character:old-king" },
            new KnowerSpec { Who = "character:ignis", State = "Unrecognized" },
            new KnowerSpec { Who = "author" },
        ];

        Assert.Empty(KnowerSpecs.Validate(knowers, "knowers"));
        Assert.Empty(KnowerSpecs.Validate(null, "knowers"));
    }

    [Fact]
    public void Validate_EveryProblem_IsLocatedByPositionAndKnower()
    {
        KnowerSpec?[] knowers =
        [
            null,
            new KnowerSpec { Who = "party", KnownAs = "two\nlines" },
            new KnowerSpec { Who = "character:belmakor", Session = -1, Note = new string('n', 1_001) },
            new KnowerSpec { Who = "character:Belmakor", How = "   " },
            new KnowerSpec { State = "knows" },
        ];

        var problems = KnowerSpecs.Validate(knowers, "knowers");

        Assert.Equal(
            [
                "knowers item 1 is null; give {\"who\": \"party\", \"state\": \"knows\"}.",
                "knowers item 2 (party): known_as must be one line of at most 200 characters.",
                "knowers item 3 (character:belmakor): session is -1; it is a session number, 0 to 100000.",
                "knowers item 3 (character:belmakor): note is longer than 1000 characters.",
                "knowers item 4 (character:belmakor): character:belmakor is listed twice; one row per knower.",
                "knowers item 4 (character:belmakor): how is blank; give text or leave it out.",
                "knowers item 5: who is required: party, table, public, dm, author or character:<handle> (e.g. \"character:belmakor\").",
            ],
            problems);
    }

    [Fact]
    public void Validate_EmptyList_IsRefused()
    {
        Assert.Equal(
            ["knowers is empty; give knowers such as [{\"who\": \"party\", \"state\": \"knows\"}] or leave it out."],
            KnowerSpecs.Validate([], "knowers"));
    }

    [Fact]
    public void AttendanceValidate_GoodList_HasNoProblems()
    {
        AttendanceSpec?[] attendance =
        [
            new AttendanceSpec { Character = "character:belmakor" },
            new AttendanceSpec { Character = "serif", Present = false, Note = "not yet in the party" },
            new AttendanceSpec { Character = "e:7", Present = true },
        ];

        Assert.Empty(AttendanceSpecs.Validate(attendance, "attendance"));
        Assert.Empty(AttendanceSpecs.Validate(null, "attendance"));
    }

    [Fact]
    public void AttendanceValidate_Problems_AreLocatedByPosition()
    {
        AttendanceSpec?[] attendance =
        [
            new AttendanceSpec { Character = "character:serif" },
            new AttendanceSpec { Character = "Serif", Present = false },
            null,
            new AttendanceSpec(),
            new AttendanceSpec { Character = "location:flotsam" },
            new AttendanceSpec { Character = "f:3" },
        ];

        var problems = AttendanceSpecs.Validate(attendance, "attendance");

        Assert.Equal(
            [
                "attendance item 2: serif is listed twice.",
                "attendance item 3 is null; give {\"character\": \"character:serif\", \"present\": false}.",
                "attendance item 4: character is required, e.g. \"character:serif\".",
                "attendance item 5: character \"location:flotsam\" must name a character, e.g. \"character:serif\".",
                "attendance item 6: character \"f:3\" must name a character, e.g. \"character:serif\".",
            ],
            problems);
        Assert.Single(AttendanceSpecs.Validate([], "attendance"));
    }
}
