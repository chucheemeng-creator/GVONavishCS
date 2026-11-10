// CoordFilterTests.cs
// 擋掉讀錯的座標：跳太遠先不採用，連續兩次在新位置才接受。

using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class CoordFilterTests
{
    private static readonly WorldPoint Home = new(1000, 1000);

    /// <summary>已經確認在 Home 的過濾器（時間 500 毫秒）。</summary>
    private static CoordFilter StartedAtHome()
    {
        CoordFilter f = new();
        f.Feed(Home, 0);
        f.Feed(Home, 500);
        return f;
    }

    [Fact]
    public void 剛開始_連續兩次相近才採用()
    {
        CoordFilter f = new();
        Assert.Equal(CoordFilterResult.Rejected, f.Feed(Home, 0));
        Assert.Null(f.Current);
        Assert.Equal(CoordFilterResult.Accepted, f.Feed(new WorldPoint(1001, 1000), 500));
        Assert.Equal(new WorldPoint(1001, 1000), f.Current);
        Assert.Equal(0, f.RejectedCount); // 剛開始等確認的那一次不算「擋掉」
    }

    [Fact]
    public void 正常移動_採用()
    {
        CoordFilter f = StartedAtHome();
        Assert.Equal(CoordFilterResult.Accepted, f.Feed(new WorldPoint(1003, 1002), 1000));
        Assert.Equal(new WorldPoint(1003, 1002), f.Current);
    }

    [Fact]
    public void 讀錯一次_擋掉_之後照常()
    {
        CoordFilter f = StartedAtHome();
        Assert.Equal(CoordFilterResult.Rejected, f.Feed(new WorldPoint(7000, 3000), 1000));
        Assert.Equal(Home, f.Current);
        Assert.Equal(1, f.RejectedCount);
        Assert.Equal(CoordFilterResult.Accepted, f.Feed(new WorldPoint(1002, 1000), 1500));
    }

    [Fact]
    public void 真的換了位置_連續兩次在新位置就接受()
    {
        CoordFilter f = StartedAtHome();
        Assert.Equal(CoordFilterResult.Rejected, f.Feed(new WorldPoint(7000, 3000), 1000));
        Assert.Equal(CoordFilterResult.AcceptedJump, f.Feed(new WorldPoint(7001, 3000), 1500));
        Assert.Equal(new WorldPoint(7001, 3000), f.Current);
    }

    [Fact]
    public void 兩次讀錯在不同地方_都擋掉()
    {
        CoordFilter f = StartedAtHome();
        Assert.Equal(CoordFilterResult.Rejected, f.Feed(new WorldPoint(7000, 3000), 1000));
        Assert.Equal(CoordFilterResult.Rejected, f.Feed(new WorldPoint(3000, 7000), 1500));
        Assert.Equal(Home, f.Current);
    }

    [Fact]
    public void 超出世界範圍_一定是讀錯()
    {
        CoordFilter f = StartedAtHome();
        Assert.Equal(CoordFilterResult.Invalid, f.Feed(new WorldPoint(20000, 1000), 1000));
        Assert.Equal(CoordFilterResult.Invalid, f.Feed(new WorldPoint(1000, 9000), 1500));
        Assert.Equal(2, f.RejectedCount);
        Assert.Equal(Home, f.Current);
    }

    [Fact]
    public void 很久沒讀到_可移動的距離最多算十秒()
    {
        // 40 座標/秒 × 10 秒 + 5 = 405：停了一小時也只放行這麼遠
        CoordFilter f = StartedAtHome();
        Assert.Equal(CoordFilterResult.Rejected, f.Feed(new WorldPoint(1500, 1000), 3_600_000));

        CoordFilter g = StartedAtHome();
        Assert.Equal(CoordFilterResult.Accepted, g.Feed(new WorldPoint(1400, 1000), 3_600_000));
    }

    [Fact]
    public void 跨過地圖邊界的正常移動_採用()
    {
        CoordFilter f = new();
        f.Feed(new WorldPoint(16382, 500), 0);
        f.Feed(new WorldPoint(16383, 500), 500);
        Assert.Equal(CoordFilterResult.Accepted, f.Feed(new WorldPoint(1, 500), 1000));
    }

    [Fact]
    public void Reset之後_重新確認()
    {
        CoordFilter f = StartedAtHome();
        f.Reset();
        Assert.Null(f.Current);
        Assert.Equal(CoordFilterResult.Rejected, f.Feed(new WorldPoint(7000, 3000), 1000));
        Assert.Equal(CoordFilterResult.Accepted, f.Feed(new WorldPoint(7000, 3000), 1500));
    }
}
