// SpeedMeterTests.cs
// 船速表：約 5 秒前的位置到現在位置的直線距離 ÷ 經過時間。

using System;
using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class SpeedMeterTests
{
    /// <summary>以固定的速度（座標/秒）和方向直線航行 seconds 秒，每 0.5 秒讀一次（座標取整數）。</summary>
    private static SpeedMeter Sail(double speed, double degrees, double seconds, int startX = 5000)
    {
        SpeedMeter m = new();
        double rad = degrees * Math.PI / 180;
        for (long t = 0; t <= seconds * 1000; t += 500)
        {
            double d = speed * t / 1000.0;
            int x = (int)Math.Round(startX + d * Math.Sin(rad));
            int y = (int)Math.Round(3000 - d * Math.Cos(rad));
            m.Update(new WorldPoint(((x % World.Width) + World.Width) % World.Width, y), t);
        }
        return m;
    }

    [Fact]
    public void 資料不夠時_不顯示()
    {
        SpeedMeter m = Sail(3, 90, 1.0);
        Assert.False(m.HasValue);
    }

    [Theory]
    [InlineData(90)]    // 正東
    [InlineData(0)]     // 正北
    [InlineData(45)]
    [InlineData(27)]    // 斜著走：整數座標是鋸齒狀，把每一步加起來會高估
    [InlineData(200)]
    [InlineData(333)]
    public void 各種方向_誤差在百分之八以內(int degrees)
    {
        // 座標是整數：5 秒走 15 格，頭尾各差不到 1 格，所以單次的誤差最多 7% 左右
        SpeedMeter m = Sail(3, degrees, 20);
        Assert.True(m.HasValue);
        Assert.InRange(m.UnitsPerSecond, 3 * 0.92, 3 * 1.08);
    }

    [Fact]
    public void 跨過地圖邊界_速度不會暴增()
    {
        SpeedMeter m = Sail(3, 90, 20, startX: World.Width - 30);
        Assert.InRange(m.UnitsPerSecond, 3 * 0.92, 3 * 1.08);
    }

    [Fact]
    public void 停船_速度降到零()
    {
        SpeedMeter m = Sail(3, 90, 10);
        WorldPoint stop = new(5030, 3000);
        for (long t = 10_500; t <= 17_000; t += 500) m.Update(stop, t);
        Assert.Equal(0.0, m.UnitsPerSecond, 6);
    }

    [Fact]
    public void 中斷太久_重新開始算()
    {
        SpeedMeter m = Sail(3, 90, 10);
        // 6 秒後出現在很遠的地方（進港又出港）：不能拿舊的位置來算
        m.Update(new WorldPoint(9000, 3000), 16_000);
        Assert.False(m.HasValue);
        Assert.Equal(0.0, m.UnitsPerSecond, 6);
    }
}
