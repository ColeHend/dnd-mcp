using DndMcp.Domain.Encounters;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// Invariant: every monster in srd.db, both editions, reads to a CR and XP, and its XP is the one its CR is worth by the
/// XP table (0 or 10 at CR 0); every 2024 in-lair XP is the next CR's. Encounter difficulty trusts these numbers, and
/// upstream had five wrong (the 2014 Dretch at 25 XP, the 2024 Archmage at 8,000): the corrections overlay fixes them
/// in srd.db, and this is what fails if a re-vendor or a lost correction brings one back.
/// </summary>
public sealed class SrdMonsterChallengeTests : IClassFixture<SrdIndexFixture>
{
    private readonly SrdIndexFixture _fixture;

    public SrdMonsterChallengeTests(SrdIndexFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string> Editions => new(SrdEdition.All);

    private IReadOnlyList<SrdDocument> Monsters(string edition) =>
        _fixture.Column($"SELECT slug FROM doc WHERE edition = '{edition}' AND kind = 'monster' ORDER BY slug;")
            .Select(slug => _fixture.Index.Get(edition, SrdKinds.Monster, slug)!)
            .ToList();

    [Theory]
    [MemberData(nameof(Editions))]
    public void Read_EveryMonster_HasTheXpItsCrIsWorth(string edition)
    {
        var monsters = Monsters(edition);

        Assert.Equal(edition == SrdEdition.Edition2014 ? 334 : 341, monsters.Count);
        var wrong = monsters
            .Select(m => (m.Ref, Challenge: SrdMonsterChallenge.Read(m)))
            .Where(m => !ChallengeRatingTables.IsStatBlockXp(m.Challenge.ChallengeRating, m.Challenge.Xp))
            .Select(m => $"{m.Ref}: CR {m.Challenge.ChallengeRating}, {m.Challenge.Xp} XP")
            .ToList();
        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData("2014/monster/brass-dragon-wyrmling", "1", 200)]
    [InlineData("2014/monster/deep-gnome-svirfneblin", "1/2", 100)]
    [InlineData("2014/monster/dretch", "1/4", 50)]
    [InlineData("2014/monster/riding-horse", "1/4", 50)]
    [InlineData("2024/monster/archmage", "12", 8_400)]
    [InlineData("2014/monster/ogre", "2", 450)]
    [InlineData("2024/monster/adult-red-dragon", "17", 18_000)]
    public void Read_Monster_HasItsStatBlocksCrAndXp(string reference, string cr, int xp)
    {
        var challenge = SrdMonsterChallenge.Read(_fixture.Index.Get(SrdRefParser.Parse(reference, SrdEdition.Edition2024))!);

        Assert.Equal((ChallengeRating.Parse(cr), xp), (challenge.ChallengeRating, challenge.Xp));
    }

    [Fact]
    public void Read_2024InLairXp_IsTheNextCrsXpFor29Monsters()
    {
        var withLair = Monsters(SrdEdition.Edition2024)
            .Select(m => (m.Ref, Challenge: SrdMonsterChallenge.Read(m)))
            .Where(m => m.Challenge.XpInLair is not null)
            .ToList();

        Assert.Equal(29, withLair.Count);
        Assert.All(withLair, m => Assert.Equal(ChallengeRatingTables.Xp(m.Challenge.ChallengeRating.Next!.Value), m.Challenge.XpInLair));
        Assert.Contains(withLair, m => m.Ref.ToString() == "2024/monster/adult-red-dragon" && m.Challenge.XpInLair == 20_000);
    }

    [Fact]
    public void Read_2014Monsters_HaveNoInLairXp()
    {
        Assert.All(Monsters(SrdEdition.Edition2014), m => Assert.Null(SrdMonsterChallenge.Read(m).XpInLair));
    }

    [Fact]
    public void Read_ZeroXpMonsters_AreTheStatBlocksThatSaySo()
    {
        // CR 0 is "0 or 10" XP; these are the stat blocks worth 0, which encounter maths must not round up to 10.
        var zero = SrdEdition.All
            .SelectMany(Monsters)
            .Where(m => SrdMonsterChallenge.Read(m).Xp == 0)
            .Select(m => m.Ref.ToString())
            .Order(StringComparer.Ordinal);

        Assert.Equal(["2014/monster/frog", "2014/monster/sea-horse", "2024/monster/seahorse", "2024/monster/shrieker-fungus"], zero);
    }

    [Fact]
    public void Read_NotAMonster_Throws()
    {
        var spell = _fixture.Index.Get(SrdEdition.Edition2024, SrdKinds.Spell, "fireball")!;

        Assert.Throws<ArgumentException>(() => SrdMonsterChallenge.Read(spell));
    }

    [Theory]
    [InlineData("""{"challenge_rating": 3.5, "xp": 700}""")]
    [InlineData("""{"challenge_rating": "3", "xp": 700}""")]
    [InlineData("""{"xp": 700}""")]
    [InlineData("""{"challenge_rating": 3}""")]
    [InlineData("""{"challenge_rating": 3, "xp": -1}""")]
    [InlineData("""{"challenge_rating": 3, "xp": 7.5}""")]
    [InlineData("""{"challenge_rating": 3, "xp": 700, "xp_in_lair": "900"}""")]
    public void Read_DamagedRecord_ThrowsInvalidData(string json)
    {
        var doc = new SrdDocument { Edition = SrdEdition.Edition2024, Kind = SrdKinds.Monster, Slug = "broken", Name = "Broken", Json = json };

        Assert.Throws<InvalidDataException>(() => SrdMonsterChallenge.Read(doc));
    }

    [Fact]
    public void Read_NullInLairXp_IsNone()
    {
        var doc = new SrdDocument
        {
            Edition = SrdEdition.Edition2024, Kind = SrdKinds.Monster, Slug = "x", Name = "X",
            Json = """{"challenge_rating": 3, "xp": 700, "xp_in_lair": null}""",
        };

        Assert.Null(SrdMonsterChallenge.Read(doc).XpInLair);
    }
}
