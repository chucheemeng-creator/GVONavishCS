// World.cs
// 遊戲世界座標的常數和計算工具。
// 遊戲世界寬 16384、高 8192（和原版 GVONavish.h 相同），左右兩端是相連的（繞地球一圈）。

using System;

namespace GVONavish.Core;

/// <summary>
/// 世界座標上的一個點（測量座標）。
/// record struct：Equals、GetHashCode、==、!= 由編譯器自動產生。
/// </summary>
public readonly record struct WorldPoint(int X, int Y)
{
    // record 預設顯示成「WorldPoint { X = 1, Y = 2 }」，清單和狀態列要的是「1, 2」
    public override string ToString() => $"{X}, {Y}";
}

public static class World
{
    public const int Width = 16384;
    public const int Height = 8192;

    /// <summary>座標是否在世界範圍內。</summary>
    public static bool IsValid(WorldPoint p) => p.X >= 0 && p.X < Width && p.Y >= 0 && p.Y < Height;

    /// <summary>
    /// 兩點之間的 X 差距（考慮左右相連）。回傳值在 -Width/2 ~ +Width/2 之間。
    /// 例：從 16380 往東走到 5，差距是 +9，不是 -16375。
    /// </summary>
    public static int WrappedDeltaX(int fromX, int toX)
    {
        int dx = toX - fromX;
        if (dx > Width / 2) dx -= Width;
        else if (dx < -Width / 2) dx += Width;
        return dx;
    }

    /// <summary>兩點之間的距離（考慮左右相連）。</summary>
    public static double Distance(WorldPoint a, WorldPoint b)
    {
        double dx = WrappedDeltaX(a.X, b.X);
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>把 X 座標折回 0 ~ Width 之間。</summary>
    public static double WrapX(double x)
    {
        x %= Width;
        if (x < 0) x += Width;
        return x;
    }

    /// <summary>
    /// 世界座標速度（每秒移動幾個座標單位）換算成節（knot）。
    /// 換算方式和原版相同：地球一圈 = 16384 座標，現實 1 秒 = 遊戲 0.4 小時。
    /// </summary>
    public static double ToKnots(double unitsPerSecond)
    {
        const double knotFactor = (2 * Math.PI * 6378.137) / Width / 0.4 / 1.852;
        return unitsPerSecond * knotFactor;
    }
}
