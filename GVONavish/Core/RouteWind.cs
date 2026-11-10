// RouteWind.cs
// 規劃航線在某個季節的風：哪幾段是逆風、逆風多長、考慮逆風以後相當於多遠。
//
// 做法：沿著航線每隔一小段取一點，查那裡的風向，和那一段的方向比，算出船速剩幾成。
// 船速少了一成以上的小段算逆風（地圖上標成橘色）。
// 「相當於多遠」（EffectiveLength）＝ 每一小段的長度 ÷ 那一段的船速比例（正逆風 0.72）加起來：
// 用順風時的船速去除，就是需要的時間。

using System;
using System.Collections.Generic;

namespace GVONavish.Core;

public sealed class RouteWind
{
    /// <summary>每隔多遠取一點（世界座標單位）。海域最小也有幾百，這個間隔夠細了。</summary>
    private const double Step = 8.0;

    // 每個取樣小段的終點：走到航線的哪裡、到這裡為止累計的逆風長度、累計的「相當於多遠」
    private readonly List<double> _along = [0];
    private readonly List<double> _headwind = [0];
    private readonly List<double> _effective = [0];

    /// <summary>航線全長。</summary>
    public double Total => _along[^1];

    /// <summary>逆風的航段加起來多長。</summary>
    public double HeadwindLength => _headwind[^1];

    /// <summary>考慮風向以後相當於多遠（偏向逆風的航段會變長）。</summary>
    public double EffectiveLength => _effective[^1];

    /// <summary>逆風的航段（畫地圖用）。座標和規劃航線的轉折點一樣，X 是攤開的。</summary>
    public List<(Vec2 From, Vec2 To)> HeadwindRuns { get; } = [];

    /// <summary>分析規劃航線在這個季節的風。</summary>
    public RouteWind(IReadOnlyList<PlannedRoute> legs, WindMap wind, Season season)
    {
        foreach (PlannedRoute leg in legs)
        {
            for (int i = 1; i < leg.Waypoints.Count; i++)
            {
                AddSegment(leg.Waypoints[i - 1], leg.Waypoints[i], wind, season);
            }
        }
    }

    private void AddSegment(Vec2 a, Vec2 b, WindMap wind, Season season)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0) return;

        // 這一段的航向：0 = 北，90 = 東（世界座標 Y 向下）
        double heading = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        if (heading < 0) heading += 360;

        int pieces = (int)Math.Ceiling(length / Step);
        Vec2? runStart = null; // 目前這一段逆風從哪裡開始
        for (int k = 0; k < pieces; k++)
        {
            double f0 = (double)k / pieces, f1 = (double)(k + 1) / pieces, mid = (f0 + f1) / 2;
            double piece = length / pieces;
            // 不處理風向的地方（地中海、黑海…）當成正順風：不算逆風，船速不變
            double angle = wind.DirectionAt(a.X + dx * mid, a.Y + dy * mid, season) is double windTo
                ? Sail.AngleOffWind(heading, windTo)
                : 0;
            bool head = Sail.IsHeadwind(angle);

            _along.Add(_along[^1] + piece);
            _headwind.Add(_headwind[^1] + (head ? piece : 0));
            _effective.Add(_effective[^1] + piece / Sail.ForeAndAftSpeed(angle));

            if (head)
            {
                runStart ??= new Vec2(a.X + dx * f0, a.Y + dy * f0);
            }
            else if (runStart is Vec2 start)
            {
                HeadwindRuns.Add((start, new Vec2(a.X + dx * f0, a.Y + dy * f0)));
                runStart = null;
            }
        }
        if (runStart is Vec2 last)
        {
            HeadwindRuns.Add((last, b));
        }
    }

    /// <summary>
    /// 從航線上的某個位置到終點：逆風還有多長、考慮逆風以後相當於多遠。
    /// </summary>
    /// <param name="along">已經走到航線的哪裡（<see cref="RouteProgress.Along"/>）。</param>
    public (double Headwind, double Effective) Remaining(double along)
    {
        if (_along.Count < 2) return (0, 0); // 空的航線
        along = Math.Clamp(along, 0, Total);

        // 找出 along 落在哪一個取樣小段，那一小段裡照比例算
        int i = _along.BinarySearch(along);
        if (i < 0) i = ~i;                       // 第一個 >= along 的位置
        i = Math.Clamp(i, 1, _along.Count - 1);
        double span = _along[i] - _along[i - 1];
        double f = span > 0 ? (along - _along[i - 1]) / span : 0;

        double headwindDone = _headwind[i - 1] + (_headwind[i] - _headwind[i - 1]) * f;
        double effectiveDone = _effective[i - 1] + (_effective[i] - _effective[i - 1]) * f;
        return (HeadwindLength - headwindDone, EffectiveLength - effectiveDone);
    }
}
