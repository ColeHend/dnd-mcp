using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Xunit;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: however many problems a DSL object has, the caller gets ONE <see cref="DndInputException"/>: a single
/// problem on one line, several as a list of at most five with a count of the rest, duplicates removed.
/// </summary>
public sealed class DslProblemsTests
{
    [Fact]
    public void Exception_OneProblem_IsOneLine()
    {
        Assert.Equal("Invalid build: level is 0; it is 1 to 20.", DslProblems.Exception(["level is 0; it is 1 to 20."], "build").Message);
    }

    [Fact]
    public void Exception_SeveralProblems_AreAListOfAtMostFive()
    {
        var problems = Enumerable.Range(1, 7).Select(i => $"problem {i}.").ToList();

        var message = DslProblems.Exception(problems, "target").Message;

        Assert.Equal(
            "Invalid target (7 problems):\n- problem 1.\n- problem 2.\n- problem 3.\n- problem 4.\n- problem 5.\n" +
            "- … and 2 more; fix these and send it again to see them.",
            message);
    }

    [Fact]
    public void Exception_Duplicates_AreReportedOnce()
    {
        Assert.Equal("Invalid build: same.", DslProblems.Exception(["same.", "same."], "build").Message);
        Assert.Equal("Invalid build (2 problems):\n- a.\n- b.", DslProblems.Exception(["a.", "b.", "a."], "build").Message);
    }

    [Fact]
    public void ThrowIfAny_NoProblems_DoesNothingAndAnEmptyListIsNoException()
    {
        DslProblems.ThrowIfAny([], "build");

        Assert.Throws<DndInputException>(() => DslProblems.ThrowIfAny(["x."], "build"));
        Assert.Throws<ArgumentException>(() => DslProblems.Exception([], "build"));
    }
}
