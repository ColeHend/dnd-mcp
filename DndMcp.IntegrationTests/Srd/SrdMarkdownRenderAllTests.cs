using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: every real SRD record of every kind, in both editions, renders in every format without an exception,
/// inside the output budget, and shaped the way <see cref="SrdMarkdown"/> promises.
///
/// <para>
/// Why every record rather than a sample: the vendored data's shapes drift record by record (a field that is a string
/// in 300 monsters is an array in the 301st), and a formatter exception reaches the model as the SDK's bare "An error
/// occurred invoking 'rules_get'." with nothing to act on. Rendering all ~3,000 records is cheap and is the only way to
/// know none of them does that.
/// </para>
/// </summary>
public sealed class SrdMarkdownRenderAllTests
{
    public static TheoryData<string, string> EditionKinds()
    {
        var data = new TheoryData<string, string>();
        foreach (var edition in SrdEdition.All)
        {
            foreach (var kind in SrdKinds.All.Where(k => SrdKinds.ExistsIn(k.Name, edition)))
            {
                data.Add(edition, kind.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EditionKinds))]
    public void Format_EveryDocumentOfKind_RendersConciseWithinBudgetAndUntruncated(string edition, string kind)
    {
        var docs = VendoredSrdLookup.Instance.OfKind(edition, kind);
        Assert.NotEmpty(docs);

        foreach (var doc in docs)
        {
            var text = Render(doc, SrdMarkdown.Concise);

            Assert.True(text.Length <= SrdMarkdown.MaxChars,
                $"{doc.Ref}: concise output is {text.Length} characters, over the {SrdMarkdown.MaxChars} budget.");
            Assert.DoesNotContain("[Truncated", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(EditionKinds))]
    public void Format_EveryDocumentOfKind_RendersFullWithinBudget(string edition, string kind)
    {
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(edition, kind))
        {
            var text = Render(doc, SrdMarkdown.Full);

            // The cap reserves room for its truncation note, so even a capped result stays within MaxChars.
            Assert.True(text.Length <= SrdMarkdown.MaxChars,
                $"{doc.Ref}: full output is {text.Length} characters, over the {SrdMarkdown.MaxChars} budget.");
            Assert.Contains("### Raw data", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(EditionKinds))]
    public void Body_EveryDocumentOfKind_IsNonEmptyAndUsesOnlyLevelThreeOrDeeperHeadings(string edition, string kind)
    {
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(edition, kind))
        {
            var body = Render(() => SrdMarkdown.Body(doc, VendoredSrdLookup.Instance), doc);

            Assert.False(string.IsNullOrWhiteSpace(body), $"{doc.Ref}: empty body.");
            Assert.DoesNotContain('\0', body);

            // A body sits under "## 2014" in a comparison; a # or ## heading inside it would outrank that heading.
            var badHeading = body.Split('\n').FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal) ||
                                                                     line.StartsWith("## ", StringComparison.Ordinal));
            Assert.True(badHeading is null, $"{doc.Ref}: body has a level-1 or level-2 heading: \"{badHeading}\".");
        }
    }

    [Theory]
    [MemberData(nameof(EditionKinds))]
    public void Format_EveryDocumentOfKind_StartsWithItsNameAndRef(string edition, string kind)
    {
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(edition, kind))
        {
            var text = Render(doc, SrdMarkdown.Concise);

            Assert.StartsWith($"# {doc.Name}\n", text, StringComparison.Ordinal);
            Assert.Contains($"`{doc.Ref}`", text, StringComparison.Ordinal);
        }
    }

    private static string Render(SrdDocument doc, string format) =>
        Render(() => SrdMarkdown.Format(doc, format, VendoredSrdLookup.Instance), doc);

    // Names the record in the failure: a bare exception from inside a formatter says nothing about which of 3,000 it was.
    private static string Render(Func<string> render, SrdDocument doc)
    {
        try
        {
            return render();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Rendering {doc.Ref} threw: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }
}
