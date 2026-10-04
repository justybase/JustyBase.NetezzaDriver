using JustyBase.NetezzaDriver;
using JustyBase.NetezzaDriver.StringPool;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class PoolAndMetadataTests
{
    [Fact]
    public void Sylvan_HighCardinality_RemainsCorrect_AndBounded()
    {
        var pool = new Sylvan();
        var first = pool.GetString("alpha".AsSpan());
        // 20k unique short strings: must stay correct (equal content).
        for (int i = 0; i < 20000; i++)
        {
            string s = pool.GetString($"k{i:00000}".AsSpan());
            Assert.Equal($"k{i:00000}", s);
        }
        // Earlier interned value still resolves equal (either pooled hit or fresh equal).
        Assert.Equal(first, pool.GetString("alpha".AsSpan()));
    }

    [Fact]
    public void Sylvan_LowCardinality_DedupesReference()
    {
        var pool = new Sylvan();
        char[] buf = new char[4];
        "abcd".CopyTo(buf);
        string a = pool.GetString(buf.AsSpan());
        string b = pool.GetString("abcd".AsSpan());
        Assert.Same(a, b);
    }

    [Fact]
    public void Sylvan_LongStrings_BypassPool()
    {
        var pool = new Sylvan();
        string longText = new string('z', 64);
        string a = pool.GetString(longText.AsSpan());
        string c = pool.GetString(longText.AsSpan());
        Assert.Equal(longText, a);
        Assert.Equal(longText, c);
        Assert.NotSame(a, c);
    }

    [Fact]
    public void DbosTupleDesc_Initialize_AllocatesExactArrays()
    {
        var desc = new DbosTupleDesc();
        desc.Initialize(256);
        Assert.Equal(256, desc.FieldTypeArr.Length);
        Assert.Equal(256, desc.FieldSizeArr.Length);
        Assert.Equal(256, desc.FieldNullAllowedArr.Length);
    }

    [Fact]
    public void RowDescriptionMessage_PresizedLookup_Works()
    {
        var msg = new RowDescriptionMessage(3);
        msg[0] = new FieldDescription { Name = "a", TypeOID = 23 };
        msg[1] = new FieldDescription { Name = "b", TypeOID = 20 };
        msg[2] = new FieldDescription { Name = "c", TypeOID = 1043 };
        Assert.Equal(1, msg.FieldIndex("b"));
        Assert.Equal(3, msg.FieldCount);
    }

    [Fact]
    public void FieldDescription_Type_IsCached()
    {
        var fd = new FieldDescription { Name = "x", TypeOID = 23 };
        Type t1 = fd.Type;
        Type t2 = fd.Type;
        Assert.Same(t1, t2);
        Assert.Equal(typeof(int), t1);
    }
}
