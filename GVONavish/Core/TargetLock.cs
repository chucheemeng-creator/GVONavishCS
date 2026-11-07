// TargetLock.cs
// 遊戲多開時「讀哪一個視窗」的規則（不碰 Windows，方便測試）：
//   - 還沒鎖定：選最前面的視窗（縮到最小的跳過）。
//   - 讀到有效座標：鎖定目前讀的視窗。
//   - 鎖定的視窗一段時間讀不到座標、或關掉了：解除鎖定，回到選最前面的。
// 視窗用 IntPtr（視窗代碼）表示。

using System;
using System.Collections.Generic;

namespace GVONavish.Core;

public sealed class TargetLock
{
    /// <summary>鎖定的視窗；IntPtr.Zero 表示沒有鎖定。</summary>
    public IntPtr Locked { get; private set; }

    public bool IsLocked => Locked != IntPtr.Zero;

    private IntPtr _lastLocked;   // 上一次鎖定的視窗（判斷是不是換了視窗）
    private long _lastGoodRead;   // 鎖定的視窗上一次讀到座標的時間（毫秒）

    /// <summary>
    /// 決定這次要讀哪一個視窗。windows 由最前面排到最後面。找不到任何視窗時回傳 IntPtr.Zero。
    /// </summary>
    public IntPtr Choose(IReadOnlyList<IntPtr> windows, Func<IntPtr, bool> isMinimized)
    {
        if (IsLocked && !Contains(windows, Locked))
        {
            Locked = IntPtr.Zero; // 鎖定的視窗關掉或隱藏了
        }
        if (IsLocked)
        {
            return Locked;
        }
        foreach (IntPtr h in windows)
        {
            if (!isMinimized(h))
            {
                return h;
            }
        }
        // 全部縮到最小：還是指定一個（讀不到座標，但標題、圖示取得到）
        return windows.Count > 0 ? windows[0] : IntPtr.Zero;
    }

    /// <summary>
    /// 讀完 target 之後呼叫。回傳 true 表示鎖定換成了另一個視窗（多開時換了角色）。
    /// </summary>
    public bool OnRead(IntPtr target, bool success, long nowMs, long unlockAfterMs)
    {
        if (success)
        {
            _lastGoodRead = nowMs;
            if (Locked == target)
            {
                return false;
            }
            bool changed = _lastLocked != IntPtr.Zero && _lastLocked != target;
            Locked = target;
            _lastLocked = target;
            return changed;
        }
        if (IsLocked && nowMs - _lastGoodRead > unlockAfterMs)
        {
            Locked = IntPtr.Zero; // 一陣子讀不到：解除鎖定，下次改讀最前面的
        }
        return false;
    }

    /// <summary>一開始就鎖定指定的視窗（IntPtr.Zero 表示不鎖定）。</summary>
    public void LockTo(IntPtr hwnd, long nowMs)
    {
        Locked = hwnd;
        _lastLocked = hwnd;
        _lastGoodRead = nowMs;
    }

    private static bool Contains(IReadOnlyList<IntPtr> windows, IntPtr h)
    {
        foreach (IntPtr w in windows)
        {
            if (w == h) return true;
        }
        return false;
    }
}
