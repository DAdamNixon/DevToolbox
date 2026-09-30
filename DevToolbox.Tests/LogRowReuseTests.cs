using DevToolbox.UI.Services;

namespace DevToolbox.Tests;

/// <summary>
/// Row objects carried across a refresh. The grid remembers expansions and the menu's row by object,
/// so a refresh that rebuilt every row would close them all.
/// </summary>
public class LogRowReuseTests
{
    private static Dictionary<string, string> R(string message) => new() { ["Message"] = message };

    [Fact]
    public void An_unchanged_page_comes_back_as_the_same_list()
    {
        var current = new List<Dictionary<string, string>> { R("a"), R("b") };
        var fresh = new List<Dictionary<string, string>> { R("a"), R("b") };

        Assert.Same(current, LogRowReuse.Reuse(current, fresh));
    }

    [Fact]
    public void Rows_still_on_the_page_keep_their_objects_and_new_ones_are_added()
    {
        var a = R("a");
        var b = R("b");
        var current = new List<Dictionary<string, string>> { a, b };

        var result = LogRowReuse.Reuse(current, new List<Dictionary<string, string>> { R("c"), R("a"), R("b") });

        Assert.Equal(3, result.Count);
        Assert.Equal("c", result[0]["Message"]);
        Assert.Same(a, result[1]);
        Assert.Same(b, result[2]);
    }

    [Fact]
    public void Identical_rows_never_share_one_object()
    {
        var first = R("same");
        var second = R("same");
        var current = new List<Dictionary<string, string>> { first, second };

        var result = LogRowReuse.Reuse(current, new List<Dictionary<string, string>> { R("same"), R("same"), R("same") });

        Assert.Same(first, result[0]);
        Assert.Same(second, result[1]);
        Assert.NotSame(first, result[2]);
        Assert.NotSame(second, result[2]);
    }

    [Fact]
    public void A_row_whose_content_changed_is_a_new_row()
    {
        var current = new List<Dictionary<string, string>> { R("a") };
        var result = LogRowReuse.Reuse(current, new List<Dictionary<string, string>> { R("A") });

        Assert.NotSame(current[0], result[0]);
    }
}
