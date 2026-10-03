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
}
