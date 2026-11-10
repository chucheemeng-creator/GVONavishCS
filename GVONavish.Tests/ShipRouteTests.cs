// ShipRouteTests.cs
// 航線紀錄：加點、結束一段、合併、我的最愛、超過上限時刪掉最舊的。

using System.Collections.Generic;
using System.Linq;
using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class ShipRouteTests
{
    private static ShipRoute Route(params (int X, int Y)[] points)
    {
        ShipRoute r = new();
        foreach ((int x, int y) in points) r.AddPoint(new WorldPoint(x, y));
        return r;
    }

    [Fact]
    public void 加點_累計距離_重複的點不加()
    {
        ShipRoute r = Route((0, 0), (3, 4), (3, 4), (3, 14));
        Assert.Equal(3, r.Lines[0].Count);
        Assert.Equal(15.0, r.Length, 6);
        Assert.Equal(new WorldPoint(0, 0), r.FirstPoint);
        Assert.Equal(new WorldPoint(3, 14), r.LastPoint);
    }

    [Fact]
    public void 加點_跨過地圖邊界的距離()
    {
        ShipRoute r = Route((16380, 100), (4, 100));
        Assert.Equal(8.0, r.Length, 6);
    }

    [Fact]
    public void 合併_中間的空檔也算進距離()
    {
        ShipRoute first = Route((0, 0), (10, 0));
        first.Name = "去程";
        ShipRoute second = Route((10, 5), (10, 25));
        second.JoinPrevious(first);

        Assert.Equal(2, second.Lines.Count);                 // 地圖上還是兩條線，中間不畫
        Assert.Equal(10.0 + 5.0 + 20.0, second.Length, 6);
        Assert.Equal(new WorldPoint(0, 0), second.FirstPoint);
        Assert.Equal("去程", second.Name);                   // 只有前一段有名字時沿用
    }

    [Fact]
    public void 合併_前一段是空的_不變()
    {
        ShipRoute second = Route((10, 5), (10, 25));
        second.JoinPrevious(new ShipRoute());
        Assert.Single(second.Lines);
        Assert.Equal(20.0, second.Length, 6);
    }

    [Fact]
    public void 合併_這一段是空的_變成前一段()
    {
        ShipRoute first = Route((0, 0), (10, 0));
        first.Favorite = true;
        ShipRoute second = new();
        second.JoinPrevious(first);
        Assert.Equal(10.0, second.Length, 6);
        Assert.True(second.Favorite);
        first.AddPoint(new WorldPoint(20, 0));               // 改前一段不會動到合併後的
        Assert.Equal(2, second.Lines[0].Count);
    }

    [Fact]
    public void 清單_結束一段後_下一個點開新的一段()
    {
        ShipRouteList list = new();
        list.AddPoint(new WorldPoint(0, 0));
        list.AddPoint(new WorldPoint(5, 0));
        list.CloseCurrent();
        Assert.Null(list.Current);
        list.AddPoint(new WorldPoint(100, 100));
        Assert.Equal(2, list.Routes.Count);
        Assert.NotNull(list.Current);
    }

    [Fact]
    public void 清單_只有一個點的航線_結束時丟掉()
    {
        ShipRouteList list = new();
        list.AddPoint(new WorldPoint(0, 0));
        list.CloseCurrent();
        Assert.Empty(list.Routes);
    }

    [Fact]
    public void 清單_超過上限_刪最舊的_我的最愛不刪()
    {
        ShipRouteList list = new() { MaxRoutesWithoutFavorites = 3 };
        List<ShipRoute> made = [];
        for (int i = 0; i < 5; i++)
        {
            list.AddPoint(new WorldPoint(i * 100, 0));
            list.AddPoint(new WorldPoint(i * 100 + 10, 0));
            made.Add(list.Current!);
            if (i == 0) list.SetFavorite(list.Current!, true);
            list.CloseCurrent();
        }
        Assert.Equal(4, list.Routes.Count);                  // 1 個我的最愛 + 3 個一般
        Assert.Contains(made[0], list.Routes);               // 最舊的是我的最愛，留著
        Assert.DoesNotContain(made[1], list.Routes);         // 一般航線裡最舊的被刪掉
        Assert.Equal(3, list.Routes.Count(r => !r.Favorite));
    }

    [Fact]
    public void 清單_取消我的最愛後_超過上限就刪()
    {
        ShipRouteList list = new() { MaxRoutesWithoutFavorites = 1 };
        ShipRoute a = Route((0, 0), (10, 0));
        a.Favorite = true;
        ShipRoute b = Route((0, 50), (10, 50));
        list.AddFinished(a);
        list.AddFinished(b);
        Assert.Equal(2, list.Routes.Count);
        list.SetFavorite(a, false);
        Assert.Single(list.Routes);
        Assert.Same(b, list.Routes[0]);
    }

    [Fact]
    public void 清單_存起來的航線放在記錄中的航線前面()
    {
        ShipRouteList list = new();
        list.AddPoint(new WorldPoint(0, 0));
        list.AddPoint(new WorldPoint(5, 0));
        ShipRoute recording = list.Current!;
        ShipRoute saved = Route((0, 50), (10, 50));
        list.AddFinished(saved);
        Assert.Equal(2, list.Routes.Count);
        Assert.Same(saved, list.Routes[0]);
        Assert.Same(recording, list.Routes[1]);
        Assert.Same(recording, list.Current);                // 記錄中的還是最後一個，繼續加點
    }

    [Fact]
    public void 清單_和前一段合併()
    {
        ShipRouteList list = new();
        ShipRoute a = Route((0, 0), (10, 0));
        ShipRoute b = Route((10, 0), (10, 30));
        list.AddFinished(a);
        list.AddFinished(b);
        Assert.False(list.JoinWithPrevious(a));              // 第一段前面沒有東西可以合併
        Assert.True(list.JoinWithPrevious(b));
        Assert.Single(list.Routes);
        Assert.Same(b, list.Routes[0]);
        Assert.Equal(40.0, b.Length, 6);
    }

    [Fact]
    public void 清單_改動時通知_沒變就不通知()
    {
        ShipRouteList list = new();
        ShipRoute a = Route((0, 0), (10, 0));
        list.AddFinished(a);
        int changed = 0;
        list.Changed += (s, e) => changed++;

        list.SetName(a, " 里斯本航線 ");
        Assert.Equal("里斯本航線", a.Name);
        list.SetName(a, "里斯本航線");
        list.SetHidden(a, true);
        list.SetHidden(a, true);
        list.SetAllHidden(true);
        list.SetAllHidden(false);
        Assert.Equal(3, changed);                            // 改名、隱藏、全部顯示
    }

    [Fact]
    public void 清單_清除我的最愛以外的()
    {
        ShipRouteList list = new();
        ShipRoute a = Route((0, 0), (10, 0));
        a.Favorite = true;
        list.AddFinished(a);
        list.AddFinished(Route((0, 50), (10, 50)));
        list.ClearExceptFavorites();
        Assert.Single(list.Routes);
        Assert.Same(a, list.Routes[0]);
    }
}
