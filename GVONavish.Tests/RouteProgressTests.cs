// RouteProgressTests.cs
// 船在規劃航線上的進度：還剩多少距離（狀態列的「剩餘」和預計時間用）。

using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class RouteProgressTests
{
    private static PlannedRoute Leg(params (double X, double Y)[] points)
    {
        PlannedRoute leg = new();
        foreach ((double x, double y) in points) leg.Waypoints.Add(new Vec2(x, y));
        return leg;
    }

    // 往東 100，再往南 50
    private static readonly PlannedRoute[] Corner = [Leg((1000, 1000), (1100, 1000), (1100, 1050))];

    [Fact]
    public void 沒有航線()
    {
        Assert.Null(RouteProgress.Measure([], new WorldPoint(0, 0)));
        Assert.Null(RouteProgress.Measure([new PlannedRoute()], new WorldPoint(0, 0)));
    }

    [Fact]
    public void 在起點_剩下全程()
    {
        RouteProgress p = RouteProgress.Measure(Corner, new WorldPoint(1000, 1000))!.Value;
        Assert.Equal(150.0, p.Total, 6);
        Assert.Equal(150.0, p.Remaining, 6);
        Assert.Equal(0.0, p.OffRoute, 6);
    }

    [Fact]
    public void 走到一半()
    {
        RouteProgress p = RouteProgress.Measure(Corner, new WorldPoint(1060, 1000))!.Value;
        Assert.Equal(60.0, p.Along, 6);
        Assert.Equal(90.0, p.Remaining, 6);
    }

    [Fact]
    public void 過了轉折點()
    {
        RouteProgress p = RouteProgress.Measure(Corner, new WorldPoint(1100, 1020))!.Value;
        Assert.Equal(30.0, p.Remaining, 6);
    }

    [Fact]
    public void 到終點_剩下零()
    {
        RouteProgress p = RouteProgress.Measure(Corner, new WorldPoint(1100, 1050))!.Value;
        Assert.Equal(0.0, p.Remaining, 6);
    }

    [Fact]
    public void 偏離航線_加上開回航線的距離()
    {
        // 在航線北邊 30 的地方：離航線 30，回到航線上以後還有 90
        RouteProgress p = RouteProgress.Measure(Corner, new WorldPoint(1060, 970))!.Value;
        Assert.Equal(30.0, p.OffRoute, 6);
        Assert.Equal(120.0, p.Remaining, 6);
    }

    [Fact]
    public void 還沒開到起點_加上開過去的距離()
    {
        RouteProgress p = RouteProgress.Measure(Corner, new WorldPoint(960, 1000))!.Value;
        Assert.Equal(40.0, p.OffRoute, 6);
        Assert.Equal(190.0, p.Remaining, 6);
    }

    [Fact]
    public void 好幾段_加在一起算()
    {
        PlannedRoute[] legs = [Leg((1000, 1000), (1100, 1000)), Leg((1100, 1000), (1100, 1200))];
        RouteProgress p = RouteProgress.Measure(legs, new WorldPoint(1050, 1000))!.Value;
        Assert.Equal(300.0, p.Total, 6);
        Assert.Equal(250.0, p.Remaining, 6);
    }

    [Fact]
    public void 航線跨過地圖邊界_船在邊界的另一邊()
    {
        // 航線的 X 是攤開的：從 16300 往東到 16484（就是地圖上的 100）
        PlannedRoute[] legs = [Leg((16300, 500), (16484, 500))];
        RouteProgress p = RouteProgress.Measure(legs, new WorldPoint(40, 500))!.Value;
        Assert.Equal(0.0, p.OffRoute, 6);
        Assert.Equal(60.0, p.Remaining, 6);

        // 第二段的 X 又從小的數字開始（每一段各自攤開）也沒關係
        PlannedRoute[] two = [Leg((16300, 500), (16484, 500)), Leg((100, 500), (100, 700))];
        Assert.Equal(260.0, RouteProgress.Measure(two, new WorldPoint(40, 500))!.Value.Remaining, 6);
    }

    [Fact]
    public void 來回的航線_用上一次的進度分辨去程和回程()
    {
        // A → B → A：去程和回程經過同一片海
        PlannedRoute[] legs = [Leg((1000, 1000), (1200, 1000)), Leg((1200, 1000), (1000, 1000))];
        WorldPoint ship = new(1050, 1000);

        // 第一次算（沒有上一次的進度）：當成去程
        Assert.Equal(350.0, RouteProgress.Measure(legs, ship)!.Value.Remaining, 6);
        // 上一次在去程的 40：還是去程
        Assert.Equal(350.0, RouteProgress.Measure(legs, ship, 40)!.Value.Remaining, 6);
        // 上一次已經在回程（340）：是回程，只剩 50
        Assert.Equal(50.0, RouteProgress.Measure(legs, ship, 340)!.Value.Remaining, 6);
    }

    [Fact]
    public void 來回的航線_照著走一趟_剩餘距離一路減少()
    {
        PlannedRoute[] legs = [Leg((1000, 1000), (1200, 1000)), Leg((1200, 1000), (1000, 1000))];
        double? along = null;
        double lastRemaining = double.MaxValue;
        for (int step = 0; step <= 400; step += 3)
        {
            int x = step <= 200 ? 1000 + step : 1400 - step;
            RouteProgress p = RouteProgress.Measure(legs, new WorldPoint(x, 1001), along)!.Value; // 偏 1 格
            along = p.Along;
            Assert.True(p.Remaining < lastRemaining + 1e-9, $"第 {step} 步剩餘距離變多了");
            lastRemaining = p.Remaining;
        }
        Assert.InRange(lastRemaining, 0.0, 5.0);
    }

    [Fact]
    public void 掉頭往回走_進度跟著退回去()
    {
        RouteProgress p = RouteProgress.Measure(Corner, new WorldPoint(1020, 1000), 90)!.Value;
        Assert.Equal(20.0, p.Along, 6);
    }
}
