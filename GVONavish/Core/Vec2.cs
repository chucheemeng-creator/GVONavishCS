// Vec2.cs
// 簡單的二維向量（世界座標的方向和距離）。對應原版的 GVOVector。

using System;

namespace GVONavish.Core;

public readonly struct Vec2
{
    public double X { get; }
    public double Y { get; }

    public Vec2(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>從 a 到 b 的向量。X 方向走「比較近的那一邊」（世界左右相連）。</summary>
    public static Vec2 Between(WorldPoint a, WorldPoint b)
    {
        return new Vec2(World.WrappedDeltaX(a.X, b.X), b.Y - a.Y);
    }

    public double Length => Math.Sqrt(X * X + Y * Y);

    public bool IsZero => X == 0 && Y == 0;

    public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);

    /// <summary>長度變成 1 的向量。長度為 0 時回傳 0 向量。</summary>
    public Vec2 Normalized()
    {
        double len = Length;
        return len == 0 ? new Vec2(0, 0) : new Vec2(X / len, Y / len);
    }

    /// <summary>從這個向量轉到 other 的角度（弧度，-π ~ π）。和原版 angleTo 相同的算法。</summary>
    public double AngleTo(Vec2 other)
    {
        return Math.Atan2(other.X * Y - X * other.Y, X * other.X + Y * other.Y);
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}
