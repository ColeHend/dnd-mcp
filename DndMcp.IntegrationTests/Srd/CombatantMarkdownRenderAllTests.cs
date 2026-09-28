using DndMcp.Domain.Simulation;
using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Combatants;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: every SRD monster of both editions renders in <c>rules_get</c>'s <c>combatant</c> format without an
/// exception, within <see cref="SrdMarkdown.MaxChars"/> and untruncated, with a body that uses only <c>###</c> or deeper
/// headings (it sits under <c>## 2014</c> in a comparison) and that shows every warning the stat block carries.
///
/// <para>
/// Every monster rather than a sample, for the reason <see cref="SrdMarkdownRenderAllTests"/> gives: the normalized stat
/// blocks vary monster by monster (a lich's two dozen spells, a kraken's grapples, a hydra's heads), and a formatter
/// exception reaches the model as the SDK's bare generic error. It normalizes through the shipped overrides, exactly as
/// the server's <c>StatBlockService</c> does.
/// </para>
/// </summary>
public sealed class CombatantMarkdownRenderAllTests
{
    private static readonly Lazy<MonsterNormalizer> Normalizer = new(() =>
        new MonsterNormalizer(MonsterOverrides.Load(VendoredSrdLookup.ContentRoot), SpellOverlay.Load(VendoredSrdLookup.ContentRoot)));

    public static TheoryData<string> Editions => new(SrdEdition.All);

    [Theory]
    [MemberData(nameof(Editions))]
    public void Format_EveryMonster_RendersCombatantWithinBudgetUntruncatedAndWithEveryWarning(string edition)
    {
        var monsters = VendoredSrdLookup.Instance.OfKind(edition, SrdKinds.Monster);
        Assert.True(monsters.Count > 300);
        var longest = 0;
        foreach (var doc in monsters)
        {
            StatBlock block = null!;
            string text;
            try
            {
                text = SrdMarkdown.Format(doc, SrdMarkdown.Combatant, VendoredSrdLookup.Instance, combatant: d => block = Normalizer.Value.Normalize(d, VendoredSrdLookup.Instance));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Rendering {doc.Ref} as a combatant threw: {ex.GetType().Name}: {ex.Message}", ex);
            }

            longest = Math.Max(longest, text.Length);
            Assert.True(text.Length <= SrdMarkdown.MaxChars, $"{doc.Ref}: {text.Length} characters.");
            Assert.DoesNotContain("[Truncated", text, StringComparison.Ordinal);
            Assert.StartsWith($"# {doc.Name}\n", text, StringComparison.Ordinal);
            var body = text[text.IndexOf("\n\n", StringComparison.Ordinal)..];
            Assert.DoesNotContain("\n# ", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\n## ", body, StringComparison.Ordinal);
            Assert.All(block.Warnings, w => Assert.Contains($"{w.Where} ({w.Code}): {w.Message}", text, StringComparison.Ordinal));
        }

        // Far under the cap: the view is a summary of numbers, never the SRD prose.
        Assert.True(longest < SrdMarkdown.MaxChars / 2, $"The longest combatant view is {longest} characters.");
    }
}
