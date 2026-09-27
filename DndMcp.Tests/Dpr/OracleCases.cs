using System.Globalization;
using System.Numerics;
using System.Text.Json;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Reads <c>Dpr/Fixtures/oracle/cases.json</c>: the independent Python oracle's cases (agent O; method and schema in its
/// README.md). Every expected number there is an exact fraction; the doubles here are its nearest values.
/// </summary>
internal static class OracleCases
{
    private static readonly Lazy<JsonDocument> Document = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Dpr", "Fixtures", "oracle", "cases.json"))));

    public static JsonElement Root => Document.Value.RootElement;

    public static IEnumerable<JsonElement> All => Root.GetProperty("cases").EnumerateArray();

    public static JsonElement Case(string id) => All.Single(c => c.GetProperty("id").GetString() == id);

    public static string Id(JsonElement c) => c.GetProperty("id").GetString()!;

    public static string HorizonKind(JsonElement c) => c.GetProperty("horizon").GetProperty("kind").GetString()!;

    /// <summary>"num/den" (or an integer) as the nearest double.</summary>
    public static double Exact(string text)
    {
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        return slash < 0
            ? (double)BigInteger.Parse(text, CultureInfo.InvariantCulture)
            : Fraction.ToDouble(BigInteger.Parse(text[..slash], CultureInfo.InvariantCulture), BigInteger.Parse(text[(slash + 1)..], CultureInfo.InvariantCulture));
    }

    /// <summary>An {exact, value} object's exact value.</summary>
    public static double Number(JsonElement numberObject) => Exact(numberObject.GetProperty("exact").GetString()!);

    public static bool TryGet(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    /// <summary>The case's build, target and rulings bound exactly as the tools bind them, resolved at the build's level.</summary>
    public static (ResolvedBuild Build, ResolvedTarget Target) Resolve(JsonElement c)
    {
        var spec = DslJson.Deserialize<BuildSpec>(c.GetProperty("build").GetRawText(), "build");
        var rulings = TryGet(c, "rulings", out var r) ? DslJson.Deserialize<RulingsSpec>(r.GetRawText(), "rulings") : null;
        var target = TryGet(c, "target", out var t) ? DslJson.Deserialize<TargetSpec>(t.GetRawText(), "target") : null;
        var build = BuildResolver.Resolve(spec, spec.Level!.Value, rulings);
        return (build, TargetResolver.Resolve(target, build.Level));
    }

    /// <summary>The engine's result for a round1 or fight case.</summary>
    public static DprResult Evaluate(JsonElement c)
    {
        var (build, target) = Resolve(c);
        var horizon = c.GetProperty("horizon");
        var options = HorizonKind(c) == DprHorizons.Round1
            ? DprOptions.Round1
            : DprOptions.Fight(horizon.GetProperty("rounds").GetInt32());
        return DprEngine.Evaluate(build, target, options, new WorkMeter(DprLimits.WorkBudget));
    }
}
