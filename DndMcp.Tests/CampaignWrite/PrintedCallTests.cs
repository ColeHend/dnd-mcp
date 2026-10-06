using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignCharacters;
using DndMcp.Tests.CampaignCombat;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant (F1, one order everywhere): every call the Repository prints, in a result line, a reminder or a refusal, is
/// valid JSON once its bare "…" placeholders are filled, and names the campaign it is about LAST
/// (<c>{"action": …, …, "campaign": "&lt;slug&gt;"}</c>): a printed call goes to whichever campaign is current when the
/// model sends it, so one that names no campaign can land in another, and the host's calls, C's reminders and the
/// tracker's calls (completed by the host) all put it last. The sweep runs the Repository's own printing paths in the
/// fixture worlds: combat's lifecycle and refusals, the store's unreadable-row refusal, the character writer's refusals and
/// reminders (routed ones too), the undo guard, the session reader and the knowledge warnings.
/// </summary>
public sealed class PrintedCallTests
{
    // A printed call: a campaign tool's name, a space and a JSON object (found by matching its braces outside strings).
    private static readonly Regex CallStart =
        new(@"(?<![A-Za-z0-9_])(?:combat|campaign_[a-z]+|campaign|balance_simulate|encounter_difficulty) \{", RegexOptions.CultureInvariant);

    /// <summary>The printed calls in <paramref name="text"/>, each parsed (a bare "…" read as null).</summary>
    internal static IReadOnlyList<(string Tool, JsonObject Arguments)> In(string text)
    {
        var calls = new List<(string, JsonObject)>();
        foreach (Match match in CallStart.Matches(text))
        {
            var open = match.Index + match.Length - 1;
            var close = ClosingBrace(text, open);
            Assert.True(close > open, $"A printed call never closes: {text[match.Index..]}");
            var json = Regex.Replace(text[open..(close + 1)], "(?<=: )…", "null");
            calls.Add((match.Value[..^2], JsonNode.Parse(json)!.AsObject()));
        }

        return calls;
    }

    [Fact]
    public void EveryCallTheRepositoryPrints_NamesItsCampaignLast()
    {
        var texts = new List<(string Where, string Text)>();
        Combat(texts);
        Characters(texts);
        Undo(texts);
        Sessions(texts);

        var calls = texts.SelectMany(t => In(t.Text).Select(c => (t.Where, c.Tool, c.Arguments))).ToList();

        Assert.True(calls.Count >= 20, $"only {calls.Count} printed calls were swept");
        Assert.All(texts, t => Assert.NotEmpty(In(t.Text)));
        Assert.All(calls, c => Assert.True(c.Arguments.Last().Key == "campaign", $"{c.Where}: {c.Tool} {c.Arguments.ToJsonString()}"));
    }

    // Combat: the no-fight refusal, the one-active refusal, prepare's line, start's no-sheet note, a running fight's start,
    // the second end, the unreadable-row refusal (author and board).
    private static void Combat(List<(string, string)> texts)
    {
        using var w = CombatWorld.Dm();
        texts.Add(("no fight", Assert.Throws<DndInputException>(() => w.Combat.Next(w.Campaign, null)).Message));
        texts.Add(("prepare", w.Combat.Prepare(w.Campaign, new PrepareRequest("Later")).Lines[0]));
        var started = w.Combat.Start(w.Campaign, new StartRequest { Name = "Now", Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)] });
        texts.Add(("start's no-sheet note", started.Lines.Single(l => l.StartsWith("No sheet", StringComparison.Ordinal))));
        texts.Add(("one active", Assert.Throws<DndInputException>(() => w.Combat.Start(w.Campaign, new StartRequest { Name = "Another" })).Message));
        texts.Add(("already running", Assert.Throws<DndInputException>(() => w.Combat.Start(w.Campaign, new StartRequest { Encounter = "Now" })).Message));
        w.F.Db.WriteBehindTheServer("UPDATE combatant SET conditions = 'garbage' WHERE name = 'Ogre'");
        texts.Add(("unreadable", Assert.Throws<CampaignStoreUnavailableException>(() => w.Reader.State(w.Campaign)).Message));
        texts.Add(("unreadable board", Assert.Throws<CampaignStoreUnavailableException>(() => w.Reader.Board(w.Campaign, Perspective.Parse("party"))).Message));
        w.Combat.End(w.Campaign, null, new EndRequest { Discard = true });

        using var x = CombatWorld.Dm();
        x.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        x.Combat.Start(x.Campaign, new StartRequest { Name = "Twice", AddParty = false, Combatants = [new CombatantRequest { Character = "character:hero" }] });
        x.Combat.Damage(x.Campaign, null, new DamageOp(["hero"]) { Amount = 5 });
        x.Combat.End(x.Campaign, null, new EndRequest());
        texts.Add(("second end", Assert.Throws<DndInputException>(() => x.Combat.End(x.Campaign, "Twice", new EndRequest())).Message));
    }

    // The character writer: no sheet, no hit points, a death's proposal, a concentration save, a routed one, rest in a
    // fight, a missing, a deleted and a default character.
    private static void Characters(List<(string, string)> texts)
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", """{ "level": 5, "max_hp": 30, "abilities": { "con": 14 } }""");
        w.Sheet("character:sidekick", """{ "classes": [{ "class": "bard", "level": 12 }] }""");
        texts.Add(("no sheet", Assert.Throws<DndInputException>(() =>
            w.Characters.Damage(w.Campaign, "character:villain", 1, null, WriteContext.Default)).Message));
        texts.Add(("no hit points", Assert.Throws<DndInputException>(() =>
            w.Characters.Damage(w.Campaign, "character:sidekick", 1, null, WriteContext.Default)).Message));
        texts.Add(("missing character", Assert.Throws<DndInputException>(() =>
            w.Characters.Update(w.Campaign, "character:nobody", SheetWorld.Spec("""{ "level": 1 }"""), null, WriteContext.Default)).Message));
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Routed", AddParty = false, Combatants = [new CombatantRequest { Character = "character:hero" }] });
        w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["hero"]) { Spell = "Bless" });
        var routed = w.Characters.Damage(w.Campaign, "character:hero", 4, null, WriteContext.Default);
        texts.Add(("routed reminder", string.Join("\n", routed.Reminders.Select(r => r.Call))));
        texts.Add(("rest in a fight", Assert.Throws<DndInputException>(() =>
            w.Characters.Rest(w.Campaign, "character:hero", "short", null, null, w.Roller, WriteContext.Default)).Message));
        w.Combat.End(w.Campaign, null, new EndRequest { Discard = true });

        using var sheet = SheetWorld.Dm();
        sheet.Update("character:hero", """{ "level": 5, "max_hp": 30, "abilities": { "con": 14 } }""");
        sheet.F.Db.WriteBehindTheServer($"UPDATE character_sheet SET concentration = '{{\"spell\":\"Bless\"}}' WHERE entity_id = '{sheet.Id("character:hero")}'");
        var hit = sheet.Writer.Damage(sheet.Campaign, "character:hero", 6, null, WriteContext.Default);
        texts.Add(("concentration save", string.Join("\n", hit.Reminders.Select(r => r.Call))));
        var died = sheet.Writer.Damage(sheet.Campaign, "character:hero", 80, null, WriteContext.Default);
        texts.Add(("died", string.Join("\n", died.Reminders.Select(r => r.Call))));
        sheet.F.Apply(sheet.Campaign, new CampaignOpSpec { Op = "delete", Ref = "character:sidekick" });
        texts.Add(("deleted", Assert.Throws<DndInputException>(() => sheet.Update("character:sidekick", """{ "level": 2 }""")).Message));

        using var f = new WriteFixture();
        var player = f.Campaign("Mine", role: CampaignValues.Roles.Player);
        texts.Add(("no character of mine", Assert.Throws<DndInputException>(() =>
            new CharacterWriter(f.Db.Database, null).Update(player, null, SheetWorld.Spec("""{ "level": 1 }"""), null, WriteContext.Default)).Message));
    }

    // The undo guard: a batch that created an entity a fight uses and the sheet the fight was seeded from.
    private static void Undo(List<(string, string)> texts)
    {
        using var w = CombatWorld.Dm();
        var created = w.F.Apply(w.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Newcomer", Subtype = "npc", Visibility = "party" });
        var sheet = w.Sheet("character:newcomer", """{ "level": 2, "max_hp": 12 }""");
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Guarded", AddParty = false, Combatants = [new CombatantRequest { Character = "character:newcomer" }] });
        texts.Add(("undo of an entity a fight uses", Assert.Throws<DndInputException>(() => w.F.History.Undo(w.Campaign, created.BatchId!, WriteContext.Default)).Message));
        texts.Add(("undo of a sheet a fight was seeded from", Assert.Throws<DndInputException>(() => w.F.History.Undo(w.Campaign, sheet.BatchId!, WriteContext.Default)).Message));
    }

    // The session reader's not-found refusal, for the author and for another view.
    private static void Sessions(List<(string, string)> texts)
    {
        using var w = SheetWorld.Dm();
        var reader = new SessionReader(w.Database);
        texts.Add(("no session (author)", Assert.Throws<DndInputException>(() => reader.Get(w.Campaign, "9")).Message));
        texts.Add(("no session (party)", Assert.Throws<DndInputException>(() => reader.Get(w.Campaign, "9", Perspective.Parse("party"))).Message));
    }

    // The index of the brace that closes the one at open (strings and their escapes skipped), or -1.
    private static int ClosingBrace(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0)
                    {
                        return c == '}' ? i : -1;
                    }

                    break;
            }
        }

        return -1;
    }
}
