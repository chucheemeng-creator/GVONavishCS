// WorldTests.cs
// 世界座標：地圖左右相連（X 繞一圈 16384）時的距離計算。

using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class WorldTests
{
    [Theory]
    [InlineData(100, 200, 100)]
    [InlineData(200, 100, -100)]
    [InlineData(16380, 5, 9)]      // 往東跨過地圖邊界
    [InlineData(5, 16380, -9)]     // 往西跨過地圖邊界
    [InlineData(0, 8192, 8192)]    // 剛好半圈：兩邊一樣遠，算往東
    [InlineData(0, 8193, -8191)]   // 超過半圈：往西比較近
    public void X差距_走比較近的那一邊(int from, int to, int expected)
    {
        Assert.Equal(expected, World.WrappedDeltaX(from, to));
    }

    [Fact]
    public void 距離_跨過地圖邊界()
    {
        Assert.Equal(5.0, World.Distance(new WorldPoint(16382, 100), new WorldPoint(1, 104)), 6);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(16383, 8191, true)]
    [InlineData(16384, 0, false)]
    [InlineData(0, 8192, false)]
    [InlineData(-1, 0, false)]
    public void 座標在不在世界範圍內(int x, int y, bool expected)
    {
        Assert.Equal(expected, World.IsValid(new WorldPoint(x, y)));
    }

    [Theory]
    [InlineData(-1.0, 16383.0)]
    [InlineData(16384.0, 0.0)]
    [InlineData(40000.0, 7232.0)]
    public void X折回一圈以內(double x, double expected)
    {
        Assert.Equal(expected, World.WrapX(x), 6);
    }

    [Fact]
    public void 速度換算成節()
    {
        // 地球一圈 40075 公里 = 16384 座標，現實 1 秒 = 遊戲 0.4 小時 → 1 座標/秒 ≈ 3.30 節
        Assert.Equal(3.30, World.ToKnots(1.0), 2);
    }
}
