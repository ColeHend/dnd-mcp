using System.Text.Json;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// <see cref="SrdDocument.Root"/> under concurrent first reads. The index hands one document to parallel tool calls
/// (and formatters read the same linked records from several threads), so the first reads of a document race to parse
/// it.
///
/// <para>
/// What breaks: the parsed root used to be cached in a <c>JsonElement?</c> field. That is a multi-word struct, and a
/// thread reading it while another writes it can see "has a value" with the element still unset: a default
/// <see cref="JsonElement"/> whose kind is Undefined. A formatter then renders an empty entry or throws, and the
/// failure shows up only as flaky formatter tests. The root is now published once as a reference.
/// </para>
/// </summary>
public sealed class SrdDocumentTests
{
    private const int Documents = 20_000;

    // Reads of one document per thread: the first parses it, the rest overlap the other threads' first writes, which is
    // where a torn value can be seen. With the old field this finds tens of torn reads per run on an 8-core machine.
    private const int ReadsPerDocument = 200;

    private static readonly int Readers = Math.Clamp(Environment.ProcessorCount, 2, 8);

    // Every reader starts on the same fresh document at the same moment and reads it repeatedly, for many documents;
    // no read may see anything but the parsed object.
    [Fact]
    public void Root_ReadByManyThreadsAtOnce_IsAlwaysTheParsedObject()
    {
        var documents = Enumerable.Range(0, Documents).Select(i => new SrdDocument
        {
            Edition = "2024",
            Kind = "spell",
            Slug = $"spell-{i}",
            Name = $"Spell {i}",
            Json = $$"""{"index":"spell-{{i}}","name":"Spell {{i}}","level":{{i % 10}}}""",
        }).ToArray();
        var torn = 0;
        using var barrier = new Barrier(Readers);

        var readers = Enumerable.Range(0, Readers).Select(_ => new Thread(() =>
        {
            foreach (var document in documents)
            {
                barrier.SignalAndWait();
                for (var read = 0; read < ReadsPerDocument; read++)
                {
                    if (!IsParsed(document))
                    {
                        Interlocked.Increment(ref torn);
                    }
                }
            }
        })).ToList();
        readers.ForEach(t => t.Start());
        readers.ForEach(t => t.Join());

        Assert.Equal(0, torn);
    }

    private static bool IsParsed(SrdDocument document)
    {
        try
        {
            var root = document.Root;
            return root.ValueKind == JsonValueKind.Object && root.GetProperty("name").GetString() == document.Name;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
}
