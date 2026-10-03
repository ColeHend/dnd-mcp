using DndMcp.Repository.Campaign.Read;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// A knowledge check's findings split the way the check is reported (contract §9, H2: "hard flags first — other_name,
/// unknown_entity, cross_campaign, forbidden, reveals_to_audience — then the things to review"), so a golden row can say
/// "flag" or "pass" and mean the same thing the author will read. A secret at risk that the text touches is also a hard
/// flag (it is what <see cref="CheckResult.Pass"/> counts); one merely about the speaker is for review.
/// </summary>
public static class CheckFlags
{
    /// <summary>The hard flags, each as "kind:what" ("other_name:Axiom Cage", "forbidden:Keras", "secret_at_risk:f:5").</summary>
    public static IReadOnlyList<string> Hard(CheckResult result) =>
        result.Names.Where(n => n.Classification != NameClasses.Ok).Select(n => $"{n.Classification}:{n.Matched}")
            .Concat(result.Names.Where(n => n.AudienceClassification == NameClasses.RevealsToAudience).Select(n => $"{NameClasses.RevealsToAudience}:{n.Matched}"))
            .Concat(result.Forbidden.Select(f => $"forbidden:{f.Matched}"))
            .Concat(result.SecretsAtRisk.Where(s => s.Reason == RiskReasons.RelatedToText).Select(s => $"secret_at_risk:{s.Fact.Ref}"))
            .ToList();

    /// <summary>The things to review: unknown facts, mistaken beliefs, stale facts, secrets about the speaker, possible inventions.</summary>
    public static IReadOnlyList<string> ToReview(CheckResult result) =>
        result.UnknownFacts.Select(f => $"unknown:{f.Ref}")
            .Concat(result.MistakenBeliefs.Select(f => $"mistaken:{f.Ref}"))
            .Concat(result.Stale.Select(s => $"stale:{s.Ref}"))
            .Concat(result.SecretsAtRisk.Where(s => s.Reason == RiskReasons.AboutSpeaker).Select(s => $"about_speaker:{s.Fact.Ref}"))
            .Concat(result.PossibleInventions.Select(p => $"invention:{p}"))
            .ToList();
}
