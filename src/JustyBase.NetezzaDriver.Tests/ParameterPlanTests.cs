using JustyBase.NetezzaDriver;
using System.Globalization;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class ParameterPlanTests
{
    private static NzParameterCollection Named(params (string Name, object? Value)[] items)
    {
        var c = new NzParameterCollection();
        foreach (var (name, value) in items)
            c.Add(new NzParameter(name, value));
        return c;
    }

    private static NzParameterCollection Positional(params object?[] values)
    {
        var c = new NzParameterCollection();
        foreach (var v in values)
        {
            var p = new NzParameter(null, v) { IsPositional = true };
            c.Add(p);
        }
        return c;
    }

    [Fact]
    public void ParseTemplate_CachesPlan_RepeatedRender_NoRescan()
    {
        const string sql = "SELECT * FROM t WHERE a = :id AND b = :name";
        var plan = NzParameterHelper.ParseTemplate(sql);
        Assert.Equal(2, plan.Placeholders.Length);
        Assert.True(plan.HasNamed);

        var p1 = Named(("id", 1), ("name", "x"));
        string r1 = NzParameterHelper.RenderWithPlan(sql, plan, p1);
        var p2 = Named(("id", 2), ("name", "y"));
        string r2 = NzParameterHelper.RenderWithPlan(sql, plan, p2);
        Assert.Contains("1", r1, StringComparison.Ordinal);
        Assert.Contains("2", r2, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PreservesQuoteCommentDollarSemantics()
    {
        const string sql = "SELECT ':notparam', \"@notparam\", $tag$:notparam$tag$, -- :notparam\n/* :notparam */ WHERE x = :p AND y = ?";
        var plan = NzParameterHelper.ParseTemplate(sql);
        Assert.Equal(2, plan.Placeholders.Length);
        Assert.True(plan.HasNamed);
        Assert.True(plan.HasPositional);

        var p = Named(("p", 42));
        string rendered = NzParameterHelper.RenderWithPlan(sql, plan, p);
        Assert.Contains(":notparam", rendered, StringComparison.Ordinal);
        Assert.Contains("@notparam", rendered, StringComparison.Ordinal);
        Assert.Contains("?", rendered, StringComparison.Ordinal);
        Assert.Contains("42", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Positional_AppendsDirectly_ValidatesCounts()
    {
        const string sql = "SELECT * FROM t WHERE a = ? AND b = ?";
        var plan = NzParameterHelper.ParseTemplate(sql);
        string ok = NzParameterHelper.RenderWithPlan(sql, plan, Positional(1, "x'y"));
        Assert.Contains("1", ok, StringComparison.Ordinal);
        Assert.Contains("'x''y'", ok, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            NzParameterHelper.RenderWithPlan(sql, plan, Positional(1)));
        Assert.Throws<InvalidOperationException>(() =>
            NzParameterHelper.RenderWithPlan(sql, plan, Positional(1, 2, 3)));
    }

    [Fact]
    public void Render_Named_ThrowsOnMissingAndUnused()
    {
        const string sql = "SELECT :a";
        var plan = NzParameterHelper.ParseTemplate(sql);
        Assert.Throws<InvalidOperationException>(() =>
            NzParameterHelper.RenderWithPlan(sql, plan, Named(("b", 1))));
        var extra = Named(("a", 1), ("unused", 2));
        Assert.Throws<InvalidOperationException>(() =>
            NzParameterHelper.RenderWithPlan(sql, plan, extra));
    }

    [Fact]
    public void Render_Named_CaseInsensitive_NoSubstringAlloc_SpanLookup()
    {
        const string sql = "SELECT * FROM t WHERE x = :ID";
        var plan = NzParameterHelper.ParseTemplate(sql);
        string rendered = NzParameterHelper.RenderWithPlan(sql, plan, Named(("id", 7)));
        Assert.Contains("7", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Named_DuplicateParameterNames_NotReportedUnused()
    {
        const string sql = "SELECT * FROM t WHERE x = :id";
        var plan = NzParameterHelper.ParseTemplate(sql);
        string rendered = NzParameterHelper.RenderWithPlan(sql, plan, Named(("id", 1), (":id", 2), ("ID", 3)));
        Assert.Contains("1", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IntegerLiterals_UseInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            string named = NzParameterHelper.RenderWithPlan(
                "SELECT :v", NzParameterHelper.ParseTemplate("SELECT :v"), Named(("v", -123)));
            Assert.Contains("-123", named, StringComparison.Ordinal);
            string positional = NzParameterHelper.RenderWithPlan(
                "SELECT ?", NzParameterHelper.ParseTemplate("SELECT ?"), Positional(-456L));
            Assert.Contains("-456", positional, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Substitute_NoParams_ReturnsSameReference_WhenNoPlaceholder()
    {
        const string sql = "SELECT 1";
        var c = new NzParameterCollection();
        Assert.Same(sql, NzParameterHelper.SubstituteParameters(sql, c));
    }

    [Fact]
    public void Render_Named_StackallocUsedArray_IsZeroInitialized_RepeatedRenders()
    {
        // Regression test for stackalloc bool[count] without explicit Clear():
        // uninitialized stack memory must not leak between renders. The
        // stackalloc branch (count <= 128) is exercised here; repeated valid
        // renders (all used=true) must not cause a subsequent render with an
        // unused parameter to silently pass, nor cause a valid render to
        // falsely report "provided but not used".
        const string sql = "SELECT * FROM t WHERE a=:p0 AND b=:p1 AND c=:p2";
        var plan = NzParameterHelper.ParseTemplate(sql);

        for (int iter = 0; iter < 50; iter++)
        {
            // Valid render: all parameters used, must never throw.
            var valid = Named(("p0", iter), ("p1", iter + 1), ("p2", iter + 2));
            string rendered = NzParameterHelper.RenderWithPlan(sql, plan, valid);
            Assert.Contains(iter.ToString(), rendered, StringComparison.Ordinal);

            // Invalid render: extra unused parameter must ALWAYS throw,
            // even right after a valid render left stack memory full of true.
            var withExtra = Named(("p0", 1), ("p1", 2), ("p2", 3), ("unused", 4));
            var ex = Assert.Throws<InvalidOperationException>(() =>
                NzParameterHelper.RenderWithPlan(sql, plan, withExtra));
            Assert.Contains("not used", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Render_Named_HeapUsedArray_LargeCount_StillValidatesUnused()
    {
        // Exercise the heap branch (count > 128).
        var sb = new System.Text.StringBuilder("SELECT ");
        var items = new List<(string Name, object? Value)>();
        for (int i = 0; i < 130; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($":p{i}");
            items.Add(($"p{i}", i));
        }
        string sql = sb.ToString();
        var plan = NzParameterHelper.ParseTemplate(sql);
        var all = new NzParameterCollection();
        foreach (var (name, value) in items)
            all.Add(new NzParameter(name, value));
        string rendered = NzParameterHelper.RenderWithPlan(sql, plan, all);
        Assert.Contains("129", rendered, StringComparison.Ordinal);

        all.Add(new NzParameter("unused", 1));
        Assert.Throws<InvalidOperationException>(() =>
            NzParameterHelper.RenderWithPlan(sql, plan, all));
    }
}
