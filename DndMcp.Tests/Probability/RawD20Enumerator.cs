using DndMcp.Domain.Probability;

namespace DndMcp.Tests.Probability;

/// <summary>
/// The oracle for the d20 model: plays out every raw sequence of d20s an attack roll or save can produce and counts,
/// with exact integer weights, which natural face ends up counting.
///
/// <para>
/// Written from the rule text, deliberately NOT the way <see cref="D20"/> is built, so agreement means something:
/// the Lucky reroll is a separate branch taken only when a 1 actually shows (weight 1 per reroll face, against weight 20
/// for a roll that needs none), the dice are a list from which one 1 is removed and the new roll appended, and Elven
/// Accuracy is a third die only under Advantage, decided here rather than read from <see cref="D20Options"/>.
/// </para>
/// </summary>
internal static class RawD20Enumerator
{
    /// <summary>How often each natural face (index 1..20) is the kept one, out of <paramref name="Total"/> equally likely sequences.</summary>
    public sealed record KeptFaces(long[] Counts, long Total);

    public static KeptFaces Enumerate(D20Mode mode, bool lucky, bool elvenAccuracy)
    {
        var dice = mode switch
        {
            D20Mode.Normal => 1,
            D20Mode.Advantage when elvenAccuracy => 3,
            _ => 2,
        };

        var counts = new long[21];
        Visit([], dice, mode == D20Mode.Disadvantage, lucky, counts);

        var total = 1L;
        for (var i = 0; i < dice; i++)
        {
            total *= 20;
        }

        return new KeptFaces(counts, lucky ? total * 20 : total);
    }

    private static void Visit(List<int> faces, int dice, bool lowest, bool lucky, long[] counts)
    {
        if (faces.Count < dice)
        {
            for (var face = 1; face <= 20; face++)
            {
                faces.Add(face);
                Visit(faces, dice, lowest, lucky, counts);
                faces.RemoveAt(faces.Count - 1);
            }

            return;
        }

        if (lucky && faces.Contains(1))
        {
            // "Reroll the die and use the new roll": one die showing 1 goes, the new roll joins the others.
            for (var reroll = 1; reroll <= 20; reroll++)
            {
                var after = new List<int>(faces);
                after.Remove(1);
                after.Add(reroll);
                counts[lowest ? after.Min() : after.Max()] += 1;
            }

            return;
        }

        // Stands for the 20 reroll faces a Lucky roll would have had in the common denominator.
        counts[lowest ? faces.Min() : faces.Max()] += lucky ? 20 : 1;
    }
}
