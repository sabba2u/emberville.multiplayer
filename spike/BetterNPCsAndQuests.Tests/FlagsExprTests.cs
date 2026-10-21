using System.Collections.Generic;
using BetterNPCsAndQuests.Core;
using Xunit;

public class FlagsExprTests
{
    static System.Func<string, int> Reader(params (string, int)[] values)
    {
        var d = new Dictionary<string, int>();
        foreach (var (k, v) in values) d[k] = v;
        return k => d.TryGetValue(k, out var v) ? v : 0;
    }

    [Fact] public void Unset_ReadsZero() => Assert.Equal(0, new FlagStore().Get("x"));

    [Fact]
    public void Set_IsCaseInsensitive_AndStripsDollar()
    {
        var f = new FlagStore();
        f.Set("$Ellie_Met", 1);
        Assert.Equal(1, f.Get("ellie_met"));
        Assert.Equal(1, f.Get("$ELLIE_MET"));
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var f = new FlagStore();
        f.Set("a", 1);
        f.Set("b", -7);
        var g = FlagStore.Load(f.Save());
        Assert.Equal(1, g.Get("a"));
        Assert.Equal(-7, g.Get("b"));
        Assert.Equal(2, g.All.Count);
    }

    [Fact]
    public void Changed_FiresOnlyOnChange()
    {
        var f = new FlagStore();
        int fired = 0;
        f.Changed += (_, _) => fired++;
        f.Set("a", 1);
        f.Set("a", 1);
        f.Set("a", 2);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Test_Bool()
    {
        var r = Reader(("a", 1), ("b", 0));
        Assert.True(Expr.Test("$a", r));
        Assert.False(Expr.Test("not $a", r));
        Assert.True(Expr.Test("$a and not $b", r));
        Assert.False(Expr.Test("$zz", r));
        Assert.True(Expr.Test("not $zz", r));
    }

    [Fact]
    public void Test_Numbers()
    {
        var r = Reader(("x", 3), ("y", 5), ("t", 1));
        Assert.True(Expr.Test("$x >= 3", r));
        Assert.False(Expr.Test("$x > 3", r));
        Assert.True(Expr.Test("$x == 3", r));
        Assert.True(Expr.Test("$x is 3", r));
        Assert.True(Expr.Test("$x != 2", r));
        Assert.True(Expr.Test("$x < $y", r));
        Assert.False(Expr.Test("$x >= $y", r));
        Assert.True(Expr.Test("$t is true", r));
        Assert.True(Expr.Test("$x <= 3 and $t", r));
    }

    [Fact]
    public void Test_Malformed_IsFalse()
    {
        var r = Reader(("x", 3));
        Assert.False(Expr.Test("$x >>= 2", r));
        Assert.False(Expr.Test("x", r));
        Assert.True(Expr.Test("", r));
        Assert.True(Expr.Test(null, r));
    }

    [Fact]
    public void Assign()
    {
        var r = Reader(("x", 7));
        Assert.True(Expr.TryAssign("$x to true", r, out var n, out var v)); Assert.Equal(("x", 1), (n, v));
        Assert.True(Expr.TryAssign(" $a to false", r, out n, out v)); Assert.Equal(("a", 0), (n, v));
        Assert.True(Expr.TryAssign("$x to 7", r, out n, out v)); Assert.Equal(7, v);
        Assert.True(Expr.TryAssign("$x to $x + 2", r, out n, out v)); Assert.Equal(9, v);
        Assert.True(Expr.TryAssign("$x to $x - 10", r, out n, out v)); Assert.Equal(-3, v);
        Assert.False(Expr.TryAssign("nonsense", r, out _, out _));
    }

    [Fact]
    public void Assign_WithoutSpaces()
    {
        var r = Reader(("x", 7));
        Assert.True(Expr.TryAssign("$x to $x+1", r, out _, out var v)); Assert.Equal(8, v);
        Assert.True(Expr.TryAssign("$x to $x-2", r, out _, out v)); Assert.Equal(5, v);
        Assert.False(Expr.TryAssign("$x to $x+", r, out _, out _));
        Assert.False(Expr.TryAssign("$x to $x*2", r, out _, out _));
    }

    [Fact]
    public void Names_FindsAll()
    {
        var names = new HashSet<string>(Expr.Names("(if: $a and not $b_c)"));
        Assert.Equal(new HashSet<string> { "a", "b_c" }, names);
    }
}
