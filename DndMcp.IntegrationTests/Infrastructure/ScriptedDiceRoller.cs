using DndMcp.Domain.Dice;

namespace DndMcp.IntegrationTests.Infrastructure;

/// <summary>
/// An <see cref="IDiceRoller"/> that gives the faces it was handed, in order, and THROWS when it runs out: a scripted fight
/// (combat's server rolls, a rest's Hit Dice) whose every die the test chose. Register it in a server with
/// <see cref="McpServerHarness.WithExtraTools"/> (<c>builder.Services.AddSingleton&lt;IDiceRoller&gt;(roller)</c>: the last
/// registration wins), then <see cref="Enqueue"/> the faces a call will roll before making it.
///
/// <para>
/// <b>Why it throws rather than wraps around</b> (as <c>CharacterToolSetup.FixedRoller</c> does): a script whose step
/// rolled one die more than the test planned (a hidden server roll: an initiative for a combatant the test forgot, a
/// concentration save the test meant to give) would otherwise go on with a face from the start of the queue, and the
/// golden it then pins would hold a roll nobody chose. Throwing makes the step fail (the SDK's generic error, the server
/// log naming this class), and <see cref="Remaining"/> lets a test assert that every face it queued was used. A face that
/// cannot come up on the die asked for (a 7 queued for a d6) throws too: the script is wrong, not the server.
/// </para>
/// <para>
/// Thread-safe (one lock): the server may serve calls on several threads, though a script makes one call at a time.
/// </para>
/// </summary>
public sealed class ScriptedDiceRoller : IDiceRoller
{
    private readonly Queue<int> _faces = new();
    private readonly List<(int Sides, int Face)> _rolled = [];
    private readonly Lock _gate = new();

    /// <param name="faces">The first faces to give, in order (more can be queued later with <see cref="Enqueue"/>).</param>
    public ScriptedDiceRoller(params int[] faces)
    {
        Enqueue(faces);
    }

    /// <summary>Echoed with dice_roll's results, so a scripted roll is never mistaken for a real one.</summary>
    public string Source => "scripted (test)";

    /// <summary>The faces queued and not yet rolled.</summary>
    public int Remaining
    {
        get
        {
            lock (_gate)
            {
                return _faces.Count;
            }
        }
    }

    /// <summary>Every die rolled so far, in order: its sides and the face given.</summary>
    public IReadOnlyList<(int Sides, int Face)> Rolled
    {
        get
        {
            lock (_gate)
            {
                return [.. _rolled];
            }
        }
    }

    /// <summary>Adds faces to the end of the queue.</summary>
    public void Enqueue(params int[] faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        lock (_gate)
        {
            foreach (var face in faces)
            {
                _faces.Enqueue(face);
            }
        }
    }

    /// <summary>The next queued face.</summary>
    /// <exception cref="InvalidOperationException">The queue is empty, or the face cannot come up on a <paramref name="sides"/>-sided die.</exception>
    public int Roll(int sides)
    {
        lock (_gate)
        {
            if (!_faces.TryDequeue(out var face))
            {
                throw new InvalidOperationException(
                    $"ScriptedDiceRoller ran out of faces at a d{sides} (roll {_rolled.Count + 1}): the call rolled a die the test did not queue.");
            }

            if (face < 1 || face > sides)
            {
                throw new InvalidOperationException($"ScriptedDiceRoller was asked for a d{sides} and its next face is {face}: the script is wrong.");
            }

            _rolled.Add((sides, face));
            return face;
        }
    }
}
