// ShipMotionTests.cs
// 航向：用同一段直線航行的每個讀數做直線擬合；轉彎後丟掉舊的點重新推算。

using System;
using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class ShipMotionTests
{
    private double _x = 5000, _y = 3000;
    private long _time;
    private readonly ShipMotion _motion = new();

    /// <summary>朝 degrees 方向以 speed（座標/秒）走 seconds 秒，每 0.5 秒讀一次（座標取整數）。</summary>
    private void Sail(double degrees, double seconds, double speed = 3)
    {
        double rad = degrees * Math.PI / 180;
        for (int i = 0; i < seconds * 2; i++)
        {
            _x += speed * 0.5 * Math.Sin(rad);
            _y -= speed * 0.5 * Math.Cos(rad);
            _time += 500;
            int x = (((int)Math.Round(_x) % World.Width) + World.Width) % World.Width;
            _motion.Update(new WorldPoint(x, (int)Math.Round(_y)), _time);
        }
    }

    /// <summary>兩個角度差幾度（0 ~ 180）。</summary>
    private static double AngleDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    [Fact]
    public void 還沒移動_不知道航向()
    {
        _motion.Reset(new WorldPoint(5000, 3000), 0);
        Assert.Null(_motion.HeadingDegrees);
        Assert.True(_motion.Heading.IsZero);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(38)]
    [InlineData(124)]
    [InlineData(251)]
    [InlineData(346)]
    public void 直線航行十五秒_誤差在兩度以內(int degrees)
    {
        _motion.Reset(new WorldPoint(5000, 3000), 0);
        Sail(degrees, 15);
        Assert.NotNull(_motion.HeadingDegrees);
        Assert.InRange(AngleDiff(_motion.HeadingDegrees!.Value, degrees), 0.0, 2.0);
    }

    [Fact]
    public void 轉彎以後_跟上新的方向()
    {
        _motion.Reset(new WorldPoint(5000, 3000), 0);
        Sail(90, 15);
        Sail(180, 15);
        Assert.InRange(AngleDiff(_motion.HeadingDegrees!.Value, 180), 0.0, 2.0);
    }

    [Fact]
    public void 跨過地圖邊界_航向不變()
    {
        _x = World.Width - 20;
        _motion.Reset(new WorldPoint(World.Width - 20, 3000), 0);
        Sail(90, 15);
        Assert.InRange(AngleDiff(_motion.HeadingDegrees!.Value, 90), 0.0, 2.0);
    }

    [Fact]
    public void 停船_航向維持不變()
    {
        _motion.Reset(new WorldPoint(5000, 3000), 0);
        Sail(60, 15);
        double before = _motion.HeadingDegrees!.Value;
        Sail(60, 4, speed: 0);
        Assert.Equal(before, _motion.HeadingDegrees!.Value);
    }
}
