// TargetLockTests.cs
// 遊戲多開：沒鎖定時讀最前面的視窗；讀到座標就鎖定；一陣子讀不到、或視窗關掉就解除。

using System;
using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class TargetLockTests
{
    private static readonly IntPtr A = new(1), B = new(2), C = new(3);
    private const long UnlockAfter = 3000;

    private static bool NotMinimized(IntPtr h) => false;

    [Fact]
    public void 沒有遊戲視窗()
    {
        TargetLock l = new();
        Assert.Equal(IntPtr.Zero, l.Choose([], NotMinimized));
    }

    [Fact]
    public void 沒鎖定_讀最前面的()
    {
        TargetLock l = new();
        Assert.Equal(B, l.Choose([B, A, C], NotMinimized));
        Assert.False(l.IsLocked);
    }

    [Fact]
    public void 沒鎖定_跳過縮到最小的()
    {
        TargetLock l = new();
        Assert.Equal(C, l.Choose([A, C, B], h => h == A));
    }

    [Fact]
    public void 全部縮到最小_還是指定第一個()
    {
        TargetLock l = new();
        Assert.Equal(A, l.Choose([A, B], h => true));
    }

    [Fact]
    public void 讀到座標就鎖定_之後不管誰在最前面()
    {
        TargetLock l = new();
        Assert.False(l.OnRead(A, true, 0, UnlockAfter)); // 第一次鎖定不算「換了角色」
        Assert.Equal(A, l.Locked);
        Assert.Equal(A, l.Choose([B, C, A], NotMinimized));
    }

    [Fact]
    public void 讀不到座標_不鎖定()
    {
        TargetLock l = new();
        l.OnRead(A, false, 0, UnlockAfter);
        Assert.False(l.IsLocked);
    }

    [Fact]
    public void 短暫讀不到_維持鎖定()
    {
        TargetLock l = new();
        l.OnRead(A, true, 0, UnlockAfter);
        l.OnRead(A, false, 3000, UnlockAfter);
        Assert.Equal(A, l.Locked);
    }

    [Fact]
    public void 一陣子讀不到_解除鎖定_改讀最前面的()
    {
        TargetLock l = new();
        l.OnRead(A, true, 0, UnlockAfter);
        l.OnRead(A, false, 3001, UnlockAfter);
        Assert.False(l.IsLocked);
        Assert.Equal(B, l.Choose([B, A], NotMinimized));
    }

    [Fact]
    public void 解除後鎖定另一個視窗_回報換了角色()
    {
        TargetLock l = new();
        l.OnRead(A, true, 0, UnlockAfter);
        l.OnRead(A, false, 4000, UnlockAfter);
        Assert.True(l.OnRead(B, true, 4500, UnlockAfter));
        Assert.Equal(B, l.Locked);
    }

    [Fact]
    public void 解除後又鎖回同一個視窗_不算換了角色()
    {
        TargetLock l = new();
        l.OnRead(A, true, 0, UnlockAfter);
        l.OnRead(A, false, 4000, UnlockAfter);
        Assert.False(l.OnRead(A, true, 4500, UnlockAfter));
    }

    [Fact]
    public void 鎖定的視窗關掉_馬上改讀別的()
    {
        TargetLock l = new();
        l.OnRead(A, true, 0, UnlockAfter);
        Assert.Equal(C, l.Choose([C, B], NotMinimized));
        Assert.False(l.IsLocked);
    }

    [Fact]
    public void 指定鎖定的視窗()
    {
        TargetLock l = new();
        l.LockTo(B, 0);
        Assert.Equal(B, l.Choose([A, B], NotMinimized));
        Assert.False(l.OnRead(B, true, 500, UnlockAfter));
    }
}
