using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>The spill buffer that protects against client-side truncation of large tool results: save the whole
/// thing, page it back deterministically.</summary>
public class OutputStoreTests
{
    [Fact]
    public void Save_and_page_reassembles_the_full_text()
    {
        string text = string.Concat(Enumerable.Range(0, 50_000).Select(i => (char)('a' + i % 26)));
        string id = OutputStore.Save(text);

        var sb = new System.Text.StringBuilder();
        int offset = 0, guard = 0;
        while (guard++ < 1000)
        {
            var s = OutputStore.ReadSlice(id, offset, 7_000);
            Assert.Null(s.Error);
            Assert.Equal(text.Length, s.TotalChars);
            sb.Append(s.Text);
            offset = s.NextOffset;
            if (s.Done) break;
        }
        Assert.Equal(text, sb.ToString());
    }

    [Fact]
    public void Read_slice_clamps_limit_to_the_budget()
    {
        string id = OutputStore.Save(new string('x', 10));
        var s = OutputStore.ReadSlice(id, 0, int.MaxValue);
        Assert.True(s.Limit <= OutputStore.MaxChars);
        Assert.True(s.Done);
        Assert.Equal("xxxxxxxxxx", s.Text);
    }

    [Fact]
    public void Unknown_id_returns_a_clean_error_not_a_throw()
    {
        var s = OutputStore.ReadSlice("out_does_not_exist", 0, 100);
        Assert.True(s.Done);
        Assert.NotNull(s.Error);
        Assert.Equal("", s.Text);
    }

    [Fact]
    public void Set_budget_clamps_and_clears()
    {
        try
        {
            Assert.Equal(50_000, OutputStore.SetBudget(50_000));
            Assert.Equal("runtime", OutputStore.BudgetSource);
            Assert.Equal(OutputStore.FloorChars, OutputStore.SetBudget(10));       // clamped up to the floor
            Assert.Equal(OutputStore.CeilingChars, OutputStore.SetBudget(int.MaxValue)); // clamped to the ceiling
        }
        finally
        {
            OutputStore.SetBudget(0);                                              // clear the override
            Assert.NotEqual("runtime", OutputStore.BudgetSource);
        }
    }
}
