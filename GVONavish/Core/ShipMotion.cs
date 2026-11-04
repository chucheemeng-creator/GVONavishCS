// ShipMotion.cs
// 從連續讀到的座標推算船的「航向」。
//
// 為什麼航向需要時間才會穩定？
//   座標是整數，船每 0.5 秒只移動 1~2 格，單看兩點的方向誤差很大（可能差 20° 以上）。
//   要走得越遠、看越多點，方向才越準，這是整數座標本身的限制。
//
// 原版（GVOShip.cpp）的做法：只看「起點到終點」的直線方向，中間的點都沒用到，
//   而且轉彎後要等舊的移動紀錄慢慢被淘汰，航向要 5~10 秒以上才會準。
//
// 這裡的做法：直線擬合（最小平方法）
//   1. 把同一段直線航行裡的「每一個」讀數都拿來用：分別找出 X 和 Y 隨時間變化的最佳直線，
//      斜率就是船的速度向量，方向就是航向。點越多，整數的誤差越能互相抵消。
//   2. 每讀到一個新點，先看它有沒有往左或往右偏離預測的直線；連續兩點往同一邊偏超過 1.3 格，
//      就表示船轉彎了，馬上丟掉轉彎前的舊點，只留最近一小段重新開始，不用等舊資料慢慢淘汰。
//   3. 最後對齊遊戲的 2 度精度（遊戲裡船的方向是每 2 度一格）。
//
// 模擬比較（各種方向、速度、轉彎各 300 次，每 0.5 秒讀一次座標）：
//                                    航向完全準確所需時間（中位數 / 90% 的情況）
//   剛開始測量                       原版 9.0 秒 / 16.4 秒  →  這裡 3.9 秒 / 6.9 秒
//   轉彎結束後                       原版 7.9 秒 / 16.3 秒  →  這裡 5.2 秒 / 12.3 秒
//   （若遊戲畫面座標每秒才更新一次）
//   剛開始測量                       原版 9.9 秒 / 17.7 秒  →  這裡 4.9 秒 / 9.0 秒
//   轉彎結束後                       原版 8.5 秒 / 16.6 秒  →  這裡 6.5 秒 / 14.5 秒
//   誤差在 ±2° 以內所需時間：大多 2~4 秒。

using System;
using System.Collections.Generic;

namespace GVONavish.Core;

public class ShipMotion
{
    /// <summary>新的點偏離預測直線超過這麼多格，就當作船轉彎或停下了。</summary>
    public double TurnThreshold { get; set; } = 1.3;

    /// <summary>判斷轉彎後，保留最近多少秒的點當新一段的開始。</summary>
    public double KeepAfterTurnSeconds { get; set; } = 0.5;

    /// <summary>同一段直線最多看多少秒（太久以前的點沒有幫助）。</summary>
    public double MaxWindowSeconds { get; set; } = 30;

    /// <summary>這一段至少移動這麼多格，才用擬合的結果更新航向。</summary>
    public double MinMoveForHeading { get; set; } = 2.0;

    /// <summary>和上一個點隔超過這麼久（秒），就從新的點重新推算。</summary>
    public double MaxGapSeconds { get; set; } = 5;

    /// <summary>擬合出的速度低於這個值（格/秒）就當作停船，航向維持不變。</summary>
    public double MinSpeed { get; set; } = 0.3;

    // 目前這一段直線航行的讀數。X 是「攤開」的座標：跨過地圖邊界時不折回，維持連續
    private readonly List<(double T, double X, double Y)> _segment = [];
    private int _lastRawX;
    private int _lastDeviationSide; // 上一個點往哪邊偏：-1 左、+1 右、0 沒偏

    /// <summary>目前的航向（長度 1 的向量；世界座標 Y 向下）。還不知道時是 0 向量。</summary>
    public Vec2 Heading { get; private set; }

    /// <summary>航向角度（度）：0 = 正北，90 = 正東，順時針。還不知道時回傳 null。</summary>
    public double? HeadingDegrees
    {
        get
        {
            if (Heading.IsZero)
            {
                return null;
            }
            double deg = Math.Round(Math.Atan2(Heading.X, -Heading.Y) * 180.0 / Math.PI) % 360;
            return deg < 0 ? deg + 360 : deg;
        }
    }

    /// <summary>清除狀態，從這一點重新開始（剛開始、或船瞬間換到別的地方時用）。</summary>
    public void Reset(WorldPoint p, long timeMs)
    {
        _segment.Clear();
        _segment.Add((timeMs / 1000.0, p.X, p.Y));
        _lastRawX = p.X;
        _lastDeviationSide = 0;
        Heading = new Vec2(0, 0);
    }

    /// <summary>加入新的座標。</summary>
    public void Update(WorldPoint p, long timeMs)
    {
        if (_segment.Count == 0)
        {
            Reset(p, timeMs);
            return;
        }

        double t = timeMs / 1000.0;
        var last = _segment[^1];

        // 和上一個點隔太久（測量中斷、進出港），舊的點已經沒有參考價值，從這一點重新開始
        if (t - last.T > MaxGapSeconds)
        {
            Reset(p, timeMs);
            return;
        }
        double x = last.X + World.WrappedDeltaX(_lastRawX, p.X);
        double y = p.Y;
        _lastRawX = p.X;

        // 1. 船轉彎了嗎？
        //    只看「左右偏」（垂直於航向的偏離），不看「前後偏」：
        //    - 轉彎時，新的點會一直往同一邊偏離直線。
        //    - 加速、減速、停船，或遊戲畫面上的座標一頓一頓地更新時，點只會前後偏，方向沒變，不用重算。
        //    連續兩個點往同一邊偏超過門檻，才當作轉彎，丟掉轉彎前的舊點。
        if (_segment.Count >= 3 && Fit(out double vx, out double vy, out double cx, out double cy, out double ct))
        {
            double speed = Math.Sqrt(vx * vx + vy * vy);
            int side = 0;
            if (speed >= MinSpeed)
            {
                double dx = x - (cx + vx * (t - ct));
                double dy = y - (cy + vy * (t - ct));
                double sideways = (dx * vy - dy * vx) / speed; // 左右偏了幾格（正負代表哪一邊）
                if (Math.Abs(sideways) > TurnThreshold)
                {
                    side = Math.Sign(sideways);
                }
            }
            if (side != 0 && side == _lastDeviationSide)
            {
                double keepFrom = t - KeepAfterTurnSeconds;
                _segment.RemoveAll(s => s.T < keepFrom);
                side = 0; // 新的一段重新開始判斷
            }
            _lastDeviationSide = side;
        }

        // 2. 加入新點；太舊的點丟掉
        _segment.Add((t, x, y));
        while (_segment.Count > 2 && t - _segment[0].T > MaxWindowSeconds)
        {
            _segment.RemoveAt(0);
        }

        // 3. 用這一段的所有點算航向
        var first = _segment[0];
        double moved = Math.Sqrt((x - first.X) * (x - first.X) + (y - first.Y) * (y - first.Y));
        if (moved >= MinMoveForHeading && Fit(out double fvx, out double fvy, out _, out _, out _))
        {
            if (Math.Sqrt(fvx * fvx + fvy * fvy) >= MinSpeed)
            {
                Heading = RoundToGameAngle(new Vec2(fvx, fvy));
            }
        }
        else if (Heading.IsZero && moved > 0)
        {
            // 剛開始、資料還很少：先用起點到終點的方向，總比沒有好
            Heading = RoundToGameAngle(new Vec2(x - first.X, y - first.Y));
        }
    }

    /// <summary>
    /// 最小平方法：分別找出 X、Y 隨時間變化的最佳直線。
    /// 位置 = (cx, cy) + (vx, vy) × (時間 − ct)，其中 (cx, cy, ct) 是所有點的平均。
    /// </summary>
    private bool Fit(out double vx, out double vy, out double cx, out double cy, out double ct)
    {
        vx = vy = cx = cy = ct = 0;
        int n = _segment.Count;
        if (n < 2)
        {
            return false;
        }

        double sumT = 0, sumX = 0, sumY = 0;
        foreach (var s in _segment)
        {
            sumT += s.T;
            sumX += s.X;
            sumY += s.Y;
        }
        ct = sumT / n;
        cx = sumX / n;
        cy = sumY / n;

        double stt = 0, stx = 0, sty = 0;
        foreach (var s in _segment)
        {
            double dt = s.T - ct;
            stt += dt * dt;
            stx += dt * (s.X - cx);
            sty += dt * (s.Y - cy);
        }
        if (stt <= 0)
        {
            return false;
        }
        vx = stx / stt;
        vy = sty / stt;
        return true;
    }

    /// <summary>把方向對齊到最接近的 2 度。</summary>
    private static Vec2 RoundToGameAngle(Vec2 v)
    {
        if (v.IsZero)
        {
            return v;
        }
        // 從正東算起的角度（世界座標 Y 向下，所以取 -Y 變成一般數學的逆時針角度）
        double degree = Math.Atan2(-v.Y, v.X) * 180.0 / Math.PI;
        degree = Math.Round(degree / 2.0) * 2.0;
        double theta = degree * Math.PI / 180.0;
        return new Vec2(Math.Cos(theta), -Math.Sin(theta));
    }
}
