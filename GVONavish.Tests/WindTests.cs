// WindTests.cs
// 季節風向：帆和風的關係、風向資料檔、規劃航線上逆風的航段。

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class WindTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "gvonavish-wind-" + Guid.NewGuid().ToString("N") + ".bin");

    public void Dispose()
    {
        File.Delete(_file);
    }

    /// <summary>
    /// 寫一個測試用的風向檔：地圖左半邊是海域 0、右半邊是海域 1（格子 2 × 1）。
    /// </summary>
    private WindMap TwoAreas(int summerWest, int winterWest, int summerEast, int winterEast)
    {
        WriteFile(2, 1, [(summerWest, winterWest), (summerEast, winterEast)], [0, 1]);
        return WindMap.Load(_file) ?? throw new InvalidOperationException("測試用的風向檔讀不出來");
    }

    private void WriteFile(int width, int height, (int Summer, int Winter)[] areas, byte[] cells, string magic = "GVOWIND1")
    {
        using FileStream fs = File.Create(_file);
        using (BinaryWriter writer = new(fs, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes(magic));
            writer.Write(width);
            writer.Write(height);
            writer.Write(areas.Length);
            foreach ((int summer, int winter) in areas)
            {
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)summer);
                writer.Write((ushort)winter);
            }
        }
        using GZipStream gz = new(fs, CompressionLevel.Optimal);
        gz.Write(cells);
    }

    private static PlannedRoute Leg(params (double X, double Y)[] points)
    {
        PlannedRoute leg = new();
        foreach ((double x, double y) in points) leg.Waypoints.Add(new Vec2(x, y));
        return leg;
    }

    // ---- 帆和風 ----

    [Theory]
    [InlineData(90, 90, 0)]       // 往東走，風也往東吹：正順風
    [InlineData(90, 130, 40)]
    [InlineData(90, 180, 90)]     // 風橫著吹：側風
    [InlineData(90, 0, 90)]
    [InlineData(90, 270, 180)]    // 往東走，風往西吹：正逆風
    [InlineData(10, 190, 180)]
    [InlineData(350, 170, 180)]   // 跨過 0 度
    [InlineData(350, 10, 20)]
    [InlineData(10, 350, 20)]
    public void 航向和風差幾度(int heading, int windTo, int expected)
    {
        Assert.Equal((double)expected, Sail.AngleOffWind(heading, windTo), 6);
    }

    [Theory]
    [InlineData(0, 1.0)]          // 正順風
    [InlineData(60, 1.0)]
    [InlineData(90, 1.0)]         // 側風：縱帆和順風一樣快
    [InlineData(135, 0.86)]       // 側風和正逆風的中間
    [InlineData(180, 0.72)]       // 正逆風
    public void 縱帆的船速_過了側風慢慢變慢(int angle, double expected)
    {
        Assert.Equal(expected, Sail.ForeAndAftSpeed(angle), 6);
    }

    [Fact]
    public void 縱帆的船速_隨角度連續變化_不會跳一級()
    {
        double last = Sail.ForeAndAftSpeed(0);
        for (double angle = 1; angle <= 180; angle++)
        {
            double speed = Sail.ForeAndAftSpeed(angle);
            Assert.InRange(last - speed, 0.0, 0.004); // 每轉 1 度最多慢 0.4%
            last = speed;
        }
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(120, false)]      // 船速還有九成以上
    [InlineData(125, true)]       // 少了一成以上：標成逆風
    [InlineData(180, true)]
    public void 船速少一成以上才標成逆風(int angle, bool expected)
    {
        Assert.Equal(expected, Sail.IsHeadwind(angle));
    }

    // ---- 風向資料檔 ----

    [Fact]
    public void 風向檔_照位置和季節查風向()
    {
        WindMap wind = TwoAreas(90, 270, 0, 180);
        Assert.Equal(2, wind.AreaCount);
        Assert.Equal(90.0, wind.DirectionAt(100, 4000, Season.Summer)!.Value, 6);
        Assert.Equal(270.0, wind.DirectionAt(100, 4000, Season.Winter)!.Value, 6);
        Assert.Equal(0.0, wind.DirectionAt(10000, 4000, Season.Summer)!.Value, 6);
        Assert.Equal(180.0, wind.DirectionAt(10000, 4000, Season.Winter)!.Value, 6);
    }

    [Fact]
    public void 風向檔_X超出地圖會折回來()
    {
        WindMap wind = TwoAreas(90, 270, 0, 180);
        Assert.Equal(90.0, wind.DirectionAt(World.Width + 100, 4000, Season.Summer)!.Value, 6); // 攤開的座標
        Assert.Equal(0.0, wind.DirectionAt(-100, 4000, Season.Summer)!.Value, 6);
        Assert.Equal(90.0, wind.DirectionAt(100, -50, Season.Summer)!.Value, 6);                // Y 超出範圍算最近的一格
        Assert.Equal(90.0, wind.DirectionAt(100, 99999, Season.Summer)!.Value, 6);
    }

    [Fact]
    public void 風向檔_不存在或內容不對_回傳null()
    {
        Assert.Null(WindMap.Load(_file));                              // 檔案不存在

        File.WriteAllText(_file, "這不是風向檔");
        Assert.Null(WindMap.Load(_file));

        WriteFile(2, 1, [(90, 270), (0, 180)], [0, 1], magic: "GVOWIND9");
        Assert.Null(WindMap.Load(_file));                              // 開頭的標記不對

        WriteFile(2, 1, [(90, 270), (0, 400)], [0, 1]);
        Assert.Null(WindMap.Load(_file));                              // 風向超過 360 度

        WriteFile(2, 1, [(90, 270), (0, 180)], [0, 2]);
        Assert.Null(WindMap.Load(_file));                              // 格子指到不存在的海域

        WriteFile(1, 1, new (int, int)[255], [0]);
        Assert.Null(WindMap.Load(_file));                              // 海域太多（255 留給「不處理風向」）

        WriteFile(2, 1, [(90, 270), (0, 180)], [0]);
        Assert.Null(WindMap.Load(_file));                              // 格子資料不夠

        WriteFile(2, 1, [(90, 270), (0, 180)], [0, 1, 1]);
        Assert.Null(WindMap.Load(_file));                              // 格子資料太多

        WriteFile(100000, 1, [(90, 270)], [0]);
        Assert.Null(WindMap.Load(_file));                              // 大小不合理
    }

    [Fact]
    public void 風向檔_不處理風向的地方_回傳null()
    {
        WriteFile(2, 1, [(90, 270)], [0, 255]);
        WindMap wind = WindMap.Load(_file)!;
        Assert.Equal(90.0, wind.DirectionAt(100, 4000, Season.Summer)!.Value, 6);
        Assert.Null(wind.DirectionAt(10000, 4000, Season.Summer));
        Assert.Null(wind.DirectionAt(10000, 4000, Season.Winter));
    }

    [Fact]
    public void 附帶的風向檔_讀得出來_幾個海域的風向合理()
    {
        WindMap wind = TestFiles.WindMap;
        Assert.InRange(wind.AreaCount, 100, 130);

        // 加勒比海：東北信風，兩季都往西吹
        Assert.InRange(wind.DirectionAt(13300, 4100, Season.Summer)!.Value, 225.0, 315.0);
        Assert.InRange(wind.DirectionAt(13300, 4100, Season.Winter)!.Value, 225.0, 315.0);
        // 北大西洋中緯度：西風帶，兩季都往東吹
        Assert.InRange(wind.DirectionAt(14500, 2700, Season.Summer)!.Value, 45.0, 135.0);
        Assert.InRange(wind.DirectionAt(14500, 2700, Season.Winter)!.Value, 45.0, 135.0);

        // 地中海、黑海、伊比利半島外海：地圖上沒有標風向，不處理
        Assert.Null(wind.DirectionAt(400, 3200, Season.Summer));     // 地中海中部
        Assert.Null(wind.DirectionAt(1500, 2900, Season.Winter));    // 黑海
        Assert.Null(wind.DirectionAt(15700, 3250, Season.Summer));   // 里斯本外海
        Assert.NotNull(wind.DirectionAt(1800, 4000, Season.Summer)); // 紅海有標
        Assert.NotNull(wind.DirectionAt(15900, 2800, Season.Summer)); // 比斯開灣有標
    }

    // ---- 規劃航線上的逆風 ----

    [Fact]
    public void 航線_全程順風或側風_沒有逆風()
    {
        WindMap wind = TwoAreas(90, 270, 90, 270);
        RouteWind route = new([Leg((1000, 4000), (1400, 4000))], wind, Season.Summer); // 往東走，風往東吹
        Assert.Equal(400.0, route.Total, 6);
        Assert.Equal(0.0, route.HeadwindLength, 6);
        Assert.Equal(400.0, route.EffectiveLength, 6);
        Assert.Empty(route.HeadwindRuns);
    }

    [Fact]
    public void 航線_全程逆風_相當於多走百分之三十九()
    {
        WindMap wind = TwoAreas(90, 270, 90, 270);
        RouteWind route = new([Leg((1000, 4000), (1400, 4000))], wind, Season.Winter); // 往東走，風往西吹
        Assert.Equal(400.0, route.HeadwindLength, 6);
        Assert.Equal(400.0 / 0.72, route.EffectiveLength, 6);
        Assert.Single(route.HeadwindRuns);
        Assert.Equal(1000.0, route.HeadwindRuns[0].From.X, 6);
        Assert.Equal(1400.0, route.HeadwindRuns[0].To.X, 6);
    }

    [Fact]
    public void 航線_經過兩個海域_只有逆風的那一段算逆風()
    {
        // 左半邊風往東、右半邊風往西；航線從 8000 往東走到 8400，中間 8192 是海域的交界
        WindMap wind = TwoAreas(90, 90, 270, 270);
        RouteWind route = new([Leg((8000, 4000), (8400, 4000))], wind, Season.Summer);
        Assert.InRange(route.HeadwindLength, 200.0, 216.0);   // 8192 ~ 8400，取樣間隔 8
        Assert.Single(route.HeadwindRuns);
        Assert.InRange(route.HeadwindRuns[0].From.X, 8184.0, 8200.0);
        Assert.Equal(8400.0, route.HeadwindRuns[0].To.X, 6);
    }

    [Fact]
    public void 航線_經過不處理風向的地方_不算逆風()
    {
        // 左半邊風往西吹，右半邊不處理風向；航線從 8000 往東走到 8400
        WriteFile(2, 1, [(270, 270)], [0, 255]);
        WindMap wind = WindMap.Load(_file)!;
        RouteWind route = new([Leg((8000, 4000), (8400, 4000))], wind, Season.Summer);
        Assert.InRange(route.HeadwindLength, 184.0, 200.0);            // 只有 8000 ~ 8192 是逆風
        Assert.Equal(route.HeadwindLength / 0.72 + (400 - route.HeadwindLength), route.EffectiveLength, 6);
    }

    [Fact]
    public void 航線_轉彎以後變成逆風()
    {
        WindMap wind = TwoAreas(0, 0, 0, 0); // 風往北吹
        // 先往東（側風）300，再往南（逆風）200
        RouteWind route = new([Leg((1000, 4000), (1300, 4000), (1300, 4200))], wind, Season.Summer);
        Assert.Equal(500.0, route.Total, 6);
        Assert.Equal(200.0, route.HeadwindLength, 6);
        Assert.Equal(300.0 + 200.0 / 0.72, route.EffectiveLength, 6);
    }

    [Fact]
    public void 航線_斜著逆風_船速照角度算()
    {
        WindMap wind = TwoAreas(0, 0, 0, 0); // 風往北吹
        // 往東南走 45 度：和風差 135 度，船速 0.86，標成逆風
        RouteWind route = new([Leg((1000, 4000), (1300, 4300))], wind, Season.Summer);
        Assert.Equal(route.Total, route.HeadwindLength, 6);
        Assert.Equal(route.Total / 0.86, route.EffectiveLength, 6);

        // 往東偏南 20 度：和風差 110 度，船速 0.938，慢了一點但不標成逆風
        double rad = 20 * Math.PI / 180;
        RouteWind slight = new([Leg((1000, 4000), (1000 + 300 * Math.Cos(rad), 4000 + 300 * Math.Sin(rad)))], wind, Season.Summer);
        Assert.Equal(0.0, slight.HeadwindLength, 6);
        Assert.InRange(slight.EffectiveLength / slight.Total, 1.06, 1.07);
    }

    [Fact]
    public void 航線_好幾段_逆風的航段分開算()
    {
        WindMap wind = TwoAreas(0, 0, 0, 0); // 風往北吹
        RouteWind route = new(
            [Leg((1000, 4000), (1000, 4100)), Leg((1000, 4100), (1200, 4100)), Leg((1200, 4100), (1200, 4300))],
            wind, Season.Summer); // 往南、往東、往南
        Assert.Equal(300.0, route.HeadwindLength, 6);
        Assert.Equal(2, route.HeadwindRuns.Count);
    }

    [Fact]
    public void 航線_剩下的逆風和相當的距離()
    {
        WindMap wind = TwoAreas(0, 0, 0, 0); // 風往北吹
        RouteWind route = new([Leg((1000, 4000), (1300, 4000), (1300, 4200))], wind, Season.Summer);

        (double headwind, double effective) = route.Remaining(0);
        Assert.Equal(200.0, headwind, 6);
        Assert.Equal(route.EffectiveLength, effective, 6);

        (headwind, effective) = route.Remaining(150);     // 往東走到一半
        Assert.Equal(200.0, headwind, 6);
        Assert.Equal(150.0 + 200.0 / 0.72, effective, 6);

        (headwind, effective) = route.Remaining(400);     // 往南走到一半
        Assert.Equal(100.0, headwind, 6);
        Assert.Equal(100.0 / 0.72, effective, 6);

        (headwind, effective) = route.Remaining(500);     // 到終點
        Assert.Equal(0.0, headwind, 6);
        Assert.Equal(0.0, effective, 6);

        (headwind, effective) = route.Remaining(9999);    // 超過全長當成終點
        Assert.Equal(0.0, effective, 6);
    }

    [Fact]
    public void 航線_空的()
    {
        WindMap wind = TwoAreas(0, 0, 0, 0);
        RouteWind route = new([], wind, Season.Summer);
        Assert.Equal(0.0, route.Total, 6);
        Assert.Equal(0.0, route.Remaining(0).Effective, 6);
    }

    [Fact]
    public void 航線_跨過地圖邊界_X攤開也查得到風向()
    {
        // 左半邊（X 0 ~ 8191）風往西吹；航線從 16300 往東到 16484（攤開，就是地圖上的 100）
        WindMap wind = TwoAreas(270, 270, 90, 90);
        RouteWind route = new([Leg((16300, 4000), (16484, 4000))], wind, Season.Summer);
        Assert.InRange(route.HeadwindLength, 92.0, 108.0); // 過了邊界的那 100 是逆風
    }
}
