// CoordFilter.cs
// 擋掉讀錯的座標。
//
// 問題：截圖被擋住一部分、或剛好截到一半時，可能讀出完全錯的數字，
//       地圖上的船就會突然跳到很遠的地方。
//
// 做法：
//   1. 新座標和上一個「確定」的座標比，如果這段時間內船不可能跑那麼遠，就先不採用。
//   2. 但遊戲裡真的會瞬間換位置（例如重新登入、進出港口後），
//      所以「跳過去的新位置」如果下一次讀到時還在附近，就確認是真的移動，接受它。
//   3. 程式剛開始時沒有參考點，同樣要連續兩次讀到相近的位置才採用。

using System;

namespace GVONavish.Core;

public enum CoordFilterResult
{
    /// <summary>正常移動，已採用。</summary>
    Accepted,

    /// <summary>位置跳很遠，但連續兩次都在新位置，確認是真的移動，已採用。</summary>
    AcceptedJump,

    /// <summary>和之前差太遠，先不採用（等下一次確認）。</summary>
    Rejected,

    /// <summary>座標超出世界範圍，一定是讀錯了。</summary>
    Invalid,
}

public class CoordFilter
{
    /// <summary>
    /// 船每秒最多可能移動幾個座標單位。
    /// 1 座標/秒 ≈ 3.3 節，40 ≈ 130 節，遠超過任何船的速度，只用來擋明顯讀錯的值。
    /// </summary>
    public double MaxUnitsPerSecond { get; set; } = 40.0;

    /// <summary>容許的誤差（座標單位），避免讀取間隔很短時誤擋。</summary>
    public double Slack { get; set; } = 5.0;

    /// <summary>計算可移動距離時，時間差最多算多少毫秒。</summary>
    public long MaxElapsedMs { get; set; } = 10_000;

    /// <summary>最後一個確定的座標。還沒有時是 null。</summary>
    public WorldPoint? Current { get; private set; }

    /// <summary>最後一個確定座標的時間（毫秒）。</summary>
    public long CurrentTime { get; private set; }

    /// <summary>被擋下來、等待確認的座標。</summary>
    public WorldPoint? Pending { get; private set; }

    private long _pendingTime;

    /// <summary>累計被擋掉的次數（顯示用）。</summary>
    public int RejectedCount { get; private set; }

    /// <summary>
    /// 送進一個新讀到的座標。
    /// </summary>
    /// <param name="p">讀到的座標。</param>
    /// <param name="timeMs">讀到的時間（毫秒，例如 Environment.TickCount64）。</param>
    public CoordFilterResult Feed(WorldPoint p, long timeMs)
    {
        if (!World.IsValid(p))
        {
            RejectedCount++;
            return CoordFilterResult.Invalid;
        }

        // 和目前位置比：正常移動
        if (Current.HasValue && IsReachable(Current.Value, CurrentTime, p, timeMs))
        {
            Accept(p, timeMs);
            return CoordFilterResult.Accepted;
        }

        // 和上一次被擋下的座標比：連續兩次都在這附近，確認是真的
        if (Pending.HasValue && IsReachable(Pending.Value, _pendingTime, p, timeMs))
        {
            bool isFirst = !Current.HasValue;
            Accept(p, timeMs);
            return isFirst ? CoordFilterResult.Accepted : CoordFilterResult.AcceptedJump;
        }

        // 先記下來，等下一次確認
        Pending = p;
        _pendingTime = timeMs;
        // 還沒有目前位置時（剛啟動、Reset 之後），第一次讀到的座標只是在等確認，不是「跳太遠被擋掉」，
        // 所以不算進「擋掉 N」；不然每次一開程式就會顯示擋掉 1。
        // 超出地圖範圍的座標（上面的 Invalid）不管什麼時候都是讀錯，一律計數。
        if (Current.HasValue)
        {
            RejectedCount++;
        }
        return CoordFilterResult.Rejected;
    }

    /// <summary>清除所有狀態（例如換了遊戲視窗）。</summary>
    public void Reset()
    {
        Current = null;
        Pending = null;
        CurrentTime = 0;
        _pendingTime = 0;
    }

    private void Accept(WorldPoint p, long timeMs)
    {
        Current = p;
        CurrentTime = timeMs;
        Pending = null;
    }

    private bool IsReachable(WorldPoint from, long fromTime, WorldPoint to, long toTime)
    {
        // 時間差最多算 10 秒：在港口裡停很久沒讀到座標，出港後第一筆不會因為「時間很長」就什麼都放行
        double seconds = Math.Clamp(toTime - fromTime, 0, MaxElapsedMs) / 1000.0;
        double limit = MaxUnitsPerSecond * seconds + Slack;
        return World.Distance(from, to) <= limit;
    }
}
