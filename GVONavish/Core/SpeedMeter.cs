// SpeedMeter.cs
// 船速表。
//
// 原版（GVOSpeedMeter.h）是把每一步的速度做平均，但座標是整數，
// 船斜著走時每一步是鋸齒狀（例如 +1,+1 → +2,+1 → +1,+2），把每一步的長度加起來會比實際走的直線長，
// 斜向航行時速度會多算約 5~8%。
// 這裡改成：取「約 5 秒前的位置」到「現在位置」的直線距離 ÷ 經過時間，鋸齒會互相抵消，比較準。

using System.Collections.Generic;

namespace GVONavish.Core;

public class SpeedMeter
{
    /// <summary>用多長時間內的移動來算速度（毫秒）。越長越穩定，但轉彎、加減速反應越慢。</summary>
    public long WindowMs { get; set; } = 5000;

    /// <summary>和上一筆隔超過這麼久（毫秒），就重新開始計算。</summary>
    public long MaxGapMs { get; set; } = 5000;

    /// <summary>至少要有這麼長的資料才開始顯示速度（毫秒），避免剛開始數字亂跳。</summary>
    public long MinSpanMs { get; set; } = 1500;

    private readonly List<(long Time, WorldPoint Pos)> _samples = [];

    /// <summary>目前的速度（世界座標單位/秒）。</summary>
    public double UnitsPerSecond { get; private set; }

    /// <summary>目前的速度（節）。</summary>
    public double Knots => World.ToKnots(UnitsPerSecond);

    /// <summary>是否已經有足夠的資料。</summary>
    public bool HasValue { get; private set; }

    public void Update(WorldPoint p, long timeMs)
    {
        // 和上一筆隔太久（測量中斷、進出港）：舊資料不能拿來算現在的速度，重新開始
        if (_samples.Count > 0 && timeMs - _samples[^1].Time > MaxGapMs)
        {
            Reset();
        }
        _samples.Add((timeMs, p));

        // 刪掉超出時間範圍的，但保留一筆剛好超過的（讓計算範圍盡量接近 5 秒）
        while (_samples.Count > 2 && timeMs - _samples[1].Time >= WindowMs)
        {
            _samples.RemoveAt(0);
        }

        (long t0, WorldPoint p0) = _samples[0];
        long span = timeMs - t0;
        if (span < MinSpanMs)
        {
            return; // 資料還不夠，維持上一次的值
        }
        UnitsPerSecond = World.Distance(p0, p) / span * 1000.0;
        HasValue = true;
    }

    public void Reset()
    {
        _samples.Clear();
        UnitsPerSecond = 0;
        HasValue = false;
    }
}
