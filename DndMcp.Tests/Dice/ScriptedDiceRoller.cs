using DndMcp.Domain.Dice;

namespace DndMcp.Tests.Dice;

/// <summary>
/// Hand-written <see cref="IDiceRoller"/> fake (the repo uses no mocking library) that returns chosen faces and
/// records which dice were asked for. Rolling every die at its maximum or minimum, or a scripted sequence, turns
/// "does the total add up" into an exact assertion instead of a range check that a sign or off-by-one bug can slip
/// through.
/// </summary>
public sealed class ScriptedDiceRoller : IDiceRoller
{
    private readonly Func<int, int> _face;
    private readonly List<int> _requestedSides = [];

    public ScriptedDiceRoller(Func<int, int> face)
    {
        _face = face;
    }

    /// <summary>Every die shows its highest face.</summary>
    public static ScriptedDiceRoller Highest() => new(sides => sides);

    /// <summary>Every die shows 1.</summary>
    public static ScriptedDiceRoller Lowest() => new(_ => 1);

    /// <summary>Returns <paramref name="faces"/> in order; running out is a test bug and throws.</summary>
    public static ScriptedDiceRoller Sequence(params int[] faces)
    {
        var queue = new Queue<int>(faces);
        return new ScriptedDiceRoller(sides => queue.TryDequeue(out var face)
            ? face
            : throw new InvalidOperationException($"The script ran out of faces (a d{sides} was requested)."));
    }

    public string Source => "scripted (test fake)";

    /// <summary>The side count of every die rolled, in roll order.</summary>
    public IReadOnlyList<int> RequestedSides => _requestedSides;

    public int Roll(int sides)
    {
        _requestedSides.Add(sides);
        return _face(sides);
    }
}
