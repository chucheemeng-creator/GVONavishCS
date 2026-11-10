// RoutePlannerTests.cs
// 規劃航線：用真的海陸資料（seamask.bin）規劃幾條航線，檢查不會穿過陸地、距離合理。

using System;
using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class RoutePlannerTests
{
    private static readonly SeaMap Map = TestFiles.SeaMap;

    private static WorldPoint At(string port) => PortList.Find(port)!.Position;

    private static PlannedRoute Plan(string from, string to)
    {
        PlannedRoute? route = new RoutePlanner(Map).Plan(At(from), At(to));
        Assert.True(route is not null, $"{from} → {to} 規劃不出來");
        return route!;
    }

    /// <summary>
    /// 航線有沒有穿過陸地：每隔 1 個座標單位取一點檢查。
    /// 離起點、終點 20 以內的不查（港口本身可能在陸地的格子上，航線頭尾有一小段連到海上）。
    /// </summary>
    private static void AssertStaysAtSea(PlannedRoute route, string name)
    {
        Vec2 first = route.Waypoints[0], last = route.Waypoints[^1];
        for (int i = 1; i < route.Waypoints.Count; i++)
        {
            Vec2 a = route.Waypoints[i - 1], b = route.Waypoints[i];
            double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            for (double d = 0; d <= length; d += 1.0)
            {
                double x = a.X + (b.X - a.X) * d / length, y = a.Y + (b.Y - a.Y) * d / length;
                if (Math.Sqrt((x - first.X) * (x - first.X) + (y - first.Y) * (y - first.Y)) < 20) continue;
                if (Math.Sqrt((x - last.X) * (x - last.X) + (y - last.Y) * (y - last.Y)) < 20) continue;
                (int cx, int cy) = Map.CellFromWorld(x, y);
                Assert.True(Map.IsSeaCell(cx, cy), $"{name}：航線在 {x:0}, {y:0} 穿過陸地");
            }
        }
    }

    [Fact]
    public void 海陸資料_大小和內容正常()
    {
        Assert.True(Map.IsPlausible);
    }

    [Theory]
    [InlineData("里斯本", "倫敦")]
    [InlineData("倫敦", "熱那亞")]         // 要繞過伊比利半島、過直布羅陀海峽
    [InlineData("塞維爾", "亞歷山大")]     // 跨過地圖左右邊界（X 從 15904 到 1344）
    [InlineData("伊斯坦堡", "熱那亞")]     // 窄海峽
    [InlineData("里斯本", "開普敦")]
    [InlineData("里斯本", "卡利卡特")]     // 繞過好望角
    [InlineData("巴拿馬", "利馬")]
    [InlineData("長崎", "關島")]
    [InlineData("荷巴特", "旺格努伊")]
    public void 規劃得出來_不穿過陸地_距離合理(string from, string to)
    {
        PlannedRoute route = Plan(from, to);
        string name = $"{from} → {to}";
        AssertStaysAtSea(route, name);

        // 頭尾是指定的起點、終點
        Assert.Equal(At(from), route.Start);
        Assert.Equal(At(to), route.End);
        Assert.Equal(At(from).Y, route.Waypoints[0].Y, 6);
        Assert.Equal(At(to).Y, route.Waypoints[^1].Y, 6);
        Assert.Equal((double)At(to).X, World.WrapX(route.Waypoints[^1].X), 6);

        // 總距離是每一段加起來，而且不會比直線短
        double sum = 0;
        for (int i = 1; i < route.Waypoints.Count; i++)
        {
            Vec2 a = route.Waypoints[i - 1], b = route.Waypoints[i];
            sum += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        }
        Assert.Equal(sum, route.Length, 6);
        Assert.True(route.Length >= World.Distance(At(from), At(to)) - 1e-6, name + "：比直線還短");
    }

    [Fact]
    public void 直線到得了_距離接近直線()
    {
        // 里斯本到倫敦：沿著海岸往北，繞的路不多
        PlannedRoute route = Plan("里斯本", "倫敦");
        Assert.InRange(route.Length / World.Distance(At("里斯本"), At("倫敦")), 1.0, 1.5);
    }

    [Fact]
    public void 要繞過陸地_距離比直線長很多()
    {
        // 倫敦到熱那亞：直線穿過法國，實際要繞過伊比利半島、過直布羅陀海峽
        PlannedRoute route = Plan("倫敦", "熱那亞");
        Assert.True(route.Length > World.Distance(At("倫敦"), At("熱那亞")) * 2);
    }

    [Fact]
    public void 到不了的地方_回傳null()
    {
        // 中亞內陸：附近沒有海，或只有和外海不相通的湖
        Assert.Null(new RoutePlanner(Map).Plan(new WorldPoint(4000, 2300), At("里斯本")));
        Assert.Null(new RoutePlanner(Map).Plan(At("里斯本"), new WorldPoint(4000, 2300)));
    }

    [Fact]
    public void 跨過地圖邊界_走近的那一邊_X連續不折回()
    {
        PlannedRoute route = Plan("塞維爾", "亞歷山大");
        Assert.True(route.Length < World.Width / 2.0, "繞了地球另一邊");
        for (int i = 1; i < route.Waypoints.Count; i++)
        {
            Assert.True(Math.Abs(route.Waypoints[i].X - route.Waypoints[i - 1].X) < World.Width / 2.0);
        }
        Assert.True(route.Waypoints[^1].X > World.Width, "終點的 X 應該攤開到超過地圖寬度");
    }

    [Fact]
    public void 來回的距離差不多()
    {
        double there = Plan("里斯本", "開普敦").Length;
        double back = Plan("開普敦", "里斯本").Length;
        Assert.InRange(back / there, 0.98, 1.02);
    }

    [Fact]
    public void 同一個規劃器重複使用_結果一樣()
    {
        RoutePlanner planner = new(Map);
        double first = planner.Plan(At("里斯本"), At("熱那亞"))!.Length;
        planner.Plan(At("長崎"), At("關島"));
        double again = planner.Plan(At("里斯本"), At("熱那亞"))!.Length;
        Assert.Equal(first, again, 6);
    }

    [Fact]
    public void 起點和終點相同()
    {
        PlannedRoute? route = new RoutePlanner(Map).Plan(At("里斯本"), At("里斯本"));
        Assert.NotNull(route);
        Assert.InRange(route!.Length, 0.0, 40.0);
    }

    [Fact]
    public void 規劃航線的進度_走完全程剩下零()
    {
        PlannedRoute route = Plan("里斯本", "倫敦");
        PlannedRoute[] legs = [route];
        RouteProgress start = RouteProgress.Measure(legs, At("里斯本"))!.Value;
        RouteProgress end = RouteProgress.Measure(legs, At("倫敦"), start.Total)!.Value;
        Assert.Equal(route.Length, start.Total, 6);
        Assert.Equal(route.Length, start.Remaining, 6);
        Assert.Equal(0.0, end.Remaining, 6);
    }
}
