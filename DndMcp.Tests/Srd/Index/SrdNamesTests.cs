using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// <see cref="SrdNames.Key"/> decides which spellings are "the same name" for every exact lookup, and
/// <see cref="SrdNames.EditDistance"/> which names are close enough to suggest.
/// </summary>
public sealed class SrdNamesTests
{
    [Theory]
    [InlineData("Fireball", "fireball")]
    [InlineData("  Fire   Bolt  ", "fire bolt")]
    [InlineData("Dragon's Breath", "dragons breath")]          // apostrophes vanish rather than splitting the word
    [InlineData("Dragon’s Breath", "dragons breath")]          // typographic apostrophe (2024 text uses them)
    [InlineData("Dragon‘s Breath", "dragons breath")]
    [InlineData("Dragonʼs Breath", "dragons breath")]
    [InlineData("Will-o’-Wisp", "will o wisp")]
    [InlineData("Finesse (Weapon Property)", "finesse weapon property")]
    [InlineData("Succubus/Incubus", "succubus incubus")]
    [InlineData("Carpet of Flying (3 ft. × 5 ft.)", "carpet of flying 3 ft 5 ft")]
    [InlineData("Façade", "facade")]                           // diacritics folded, not dropped
    [InlineData("NAÏVE Élan", "naive elan")]
    [InlineData("Spell Scroll (1st)", "spell scroll 1st")]
    [InlineData("--", "")]
    [InlineData("", "")]
    [InlineData("ﬂaming Sphere", "flaming sphere")]            // compatibility forms fold: ligatures…
    [InlineData("ﬁre Bolt", "fire bolt")]
    [InlineData("Ｆｉｒｅｂａｌｌ", "fireball")]                      // …and fullwidth letters
    [InlineData("Fire\u00ADball", "fireball")]                  // invisible format characters join, never split
    [InlineData("Fire\u200Bball", "fireball")]
    [InlineData("\uFEFFFireball\u200D", "fireball")]
    public void Key_Name_IsLowerCaseFoldedAndSingleSpaced(string name, string expected)
    {
        Assert.Equal(expected, SrdNames.Key(name));
    }

    [Theory]
    [InlineData("fireball", "fireball", 0)]
    [InlineData("fierball", "fireball", 1)]          // one swap of neighbours is one edit
    [InlineData("firebal", "fireball", 1)]
    [InlineData("magic misile", "magic missile", 1)]
    [InlineData("fireblal", "fireball", 1)]
    [InlineData("frieball", "fireball", 1)]
    [InlineData("fyrebawl", "fireball", 2)]
    public void EditDistance_WithinTheCap_IsExact(string a, string b, int expected)
    {
        Assert.Equal(expected, SrdNames.EditDistance(a, b, 3));
        Assert.Equal(expected, SrdNames.EditDistance(b, a, 3));
    }

    // Above the cap the value only has to say "too far", and must, even when the lengths alone do not show it.
    [Theory]
    [InlineData("fireball", "lightning bolt", 2)]
    [InlineData("abcd", "wxyz", 2)]
    [InlineData("tuff", "tough", 1)]
    public void EditDistance_BeyondTheCap_IsAboveTheCap(string a, string b, int max)
    {
        Assert.True(SrdNames.EditDistance(a, b, max) > max);
    }
}
