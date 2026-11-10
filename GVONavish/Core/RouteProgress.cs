// RouteProgress.cs
// 船在規劃航線上走到哪裡了：還剩多少距離、離航線多遠。
//
// 做法：找出航線上離船最近的點，「剩餘距離」＝ 船到那個點的距離 ＋ 從那個點沿著航線到終點的距離。
//   - 船還沒開到航線上（例如還在港口外）時，剩餘距離包含開過去的那一段。
//   - 航線來回經過同一片海（例如 A → B → A）時，離船最近的地方不只一個。
//     這時用「上一次走到哪裡」來判斷：選最接近上一次進度的那個，往前走的優先。

using System;
using System.Collections.Generic;

namespace GVONavish.Core;

/// <summary>船在規劃航線上的進度（距離都是世界座標單位）。</summary>
/// <param name="Total">航線全長。</param>
/// <param name="Along">已經走到航線的哪裡（從起點沿著航線算）。</param>
/// <param name="OffRoute">船離航線多遠。</param>
public readonly record struct RouteProgress(double Total, double Along, double OffRoute)
{
    /// <summary>到終點還有多遠（包含開回航線上的那一段）。</summary>
    public double Remaining => OffRoute + (Total - Along);

    /// <summary>
    /// 離船差不多一樣近的地方（差距在這個範圍內）都算候選，再用上一次的進度挑。
    /// 座標是整數，船照著航線走也會有 1 ~ 2 格的誤差，所以不能只比誰最近。
    /// </summary>
    private const double TieTolerance = 8.0;

    /// <summary>進度比上一次少超過這麼多的候選不考慮（少一點點是船晃動、座標取整數的誤差）。</summary>
    private const double BackSlack = 20.0;

    /// <summary>
    /// 比較候選離上一次的進度多遠時，往回退的距離要乘上這個倍數（往前走的優先）。
    /// 船在折返點掉頭以後，「去程退回一點」和「回程前進一點」離得一樣近，這樣才會選回程。
    /// </summary>
    private const double BackPenalty = 3.0;

    /// <summary>
    /// 計算船在規劃航線上的進度。沒有航線時回傳 null。
    /// </summary>
    /// <param name="legs">規劃航線的每一段，照順序。</param>
    /// <param name="ship">船的位置。</param>
    /// <param name="lastAlong">上一次算出來的 <see cref="Along"/>；第一次算，或航線改過以後傳 null。</param>
    public static RouteProgress? Measure(IReadOnlyList<PlannedRoute> legs, WorldPoint ship, double? lastAlong = null)
    {
        // 第一輪：找出最近的距離，順便算全長
        double total = 0;
        double best = double.MaxValue;
        foreach (PlannedRoute leg in legs)
        {
            for (int i = 1; i < leg.Waypoints.Count; i++)
            {
                Vec2 a = leg.Waypoints[i - 1], b = leg.Waypoints[i];
                (double dist, _) = Nearest(a, b, ship);
                if (dist < best) best = dist;
                total += Distance(a, b);
            }
        }
        if (best == double.MaxValue) return null;

        // 第二輪：差不多一樣近的都是候選
        double before = 0;          // 這個線段之前的航線長度
        double nearestAlong = -1;   // 最近的那一個（沒有上一次的進度，或候選都往回退時用）
        double nearestDist = double.MaxValue;
        double chosenAlong = -1;    // 候選裡最接近上一次進度的那一個
        double chosenDist = 0;
        double chosenCost = double.MaxValue;
        foreach (PlannedRoute leg in legs)
        {
            for (int i = 1; i < leg.Waypoints.Count; i++)
            {
                Vec2 a = leg.Waypoints[i - 1], b = leg.Waypoints[i];
                double length = Distance(a, b);
                (double dist, double t) = Nearest(a, b, ship);
                double along = before + t * length;
                before += length;

                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestAlong = along;
                }
                if (lastAlong is null || dist > best + TieTolerance || along < lastAlong.Value - BackSlack) continue;
                double cost = along >= lastAlong.Value ? along - lastAlong.Value : (lastAlong.Value - along) * BackPenalty;
                if (cost < chosenCost)
                {
                    chosenCost = cost;
                    chosenAlong = along;
                    chosenDist = dist;
                }
            }
        }

        return chosenAlong >= 0
            ? new RouteProgress(total, chosenAlong, chosenDist)
            : new RouteProgress(total, nearestAlong, nearestDist);
    }

    /// <summary>
    /// 線段 a-b 上離船最近的點：回傳距離，和那個點在線段上的位置（0 = a，1 = b）。
    /// 航線的 X 是攤開的（跨過地圖邊界時不折回），所以先把船的 X 移到離這個線段最近的那一圈。
    /// </summary>
    private static (double Dist, double T) Nearest(Vec2 a, Vec2 b, WorldPoint ship)
    {
        double midX = (a.X + b.X) / 2;
        double px = ship.X + Math.Round((midX - ship.X) / World.Width) * World.Width;
        double py = ship.Y;

        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared > 0 ? Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / lengthSquared, 0, 1) : 0;
        double ex = a.X + t * dx - px, ey = a.Y + t * dy - py;
        return (Math.Sqrt(ex * ex + ey * ey), t);
    }

    private static double Distance(Vec2 a, Vec2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
