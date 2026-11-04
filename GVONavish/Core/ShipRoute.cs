// ShipRoute.cs
// 航線（一段航跡）和航線清單。移植自原版 GVOShipRoute / GVOShipRouteList。
//
// 和原版的差異：
//   - 原版把座標換成 0~1 的小數，跨越地圖左右邊界時還要把線切成兩段；
//     這裡直接存遊戲座標（整數），跨邊界的處理留給畫圖的時候做，資料比較單純。
//   - 每段航線多記了開始、結束時間，之後航線管理視窗可以用。

using System;
using System.Collections.Generic;
using System.Linq;

namespace GVONavish.Core;

public class ShipRoute
{
    /// <summary>
    /// 航線的點。通常只有一條線；「與上一段航線連接」之後會有好幾條（中間不畫線相連）。
    /// </summary>
    public List<List<WorldPoint>> Lines { get; set; } = [];

    /// <summary>名稱（在航線管理裡取的，例如「里斯本 → 開普敦」）。沒取名是空字串。</summary>
    public string Name { get; set; } = "";

    /// <summary>我的最愛（不會被自動刪除，原版只存我的最愛）。</summary>
    public bool Favorite { get; set; }

    /// <summary>不顯示在地圖上（航線管理裡取消勾選）。舊的存檔沒有這個欄位，讀進來是 false，也就是顯示。</summary>
    public bool Hidden { get; set; }

    /// <summary>在航線管理裡被選取（畫粗一點）。不存檔。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Highlight { get; set; }

    /// <summary>已結束（不會再加點）。</summary>
    public bool Closed { get; set; }

    /// <summary>總航行距離（世界座標單位）。</summary>
    public double Length { get; set; }

    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime EndTime { get; set; } = DateTime.Now;

    public bool IsEmpty => Lines.All(l => l.Count == 0);

    public WorldPoint? FirstPoint => Lines.FirstOrDefault(l => l.Count > 0)?.First();
    public WorldPoint? LastPoint => Lines.LastOrDefault(l => l.Count > 0)?.Last();

    /// <summary>加一個點。和上一點相同就不加。</summary>
    public void AddPoint(WorldPoint p)
    {
        if (Lines.Count == 0)
        {
            Lines.Add([]);
        }
        List<WorldPoint> line = Lines[^1];
        EndTime = DateTime.Now;

        if (line.Count > 0)
        {
            WorldPoint last = line[^1];
            if (last == p)
            {
                return;
            }
            Length += World.Distance(last, p);
        }
        line.Add(p);
    }

    /// <summary>把 previous 的線接到這段航線的前面（原版的「與上一段航線連接」）。</summary>
    public void JoinPrevious(ShipRoute previous)
    {
        if (previous.IsEmpty)
        {
            return;
        }
        if (IsEmpty)
        {
            Lines = [.. previous.Lines.Select(l => new List<WorldPoint>(l))];
            Length = previous.Length;
            StartTime = previous.StartTime;
            Favorite |= previous.Favorite;
            if (Name == "") Name = previous.Name;
            return;
        }

        // 兩段之間的直線距離也算進去（地圖上這段不畫線）。
        // 會斷成兩段，通常是中間測量中斷、或進港又出港：船實際上有開過這段，
        // 不算的話總距離會偏少；進港再出港時兩端幾乎同一點，多算的也只有一點點
        Length += previous.Length + World.Distance(previous.LastPoint!.Value, FirstPoint!.Value);

        List<List<WorldPoint>> merged = [.. previous.Lines.Select(l => new List<WorldPoint>(l))];
        merged.AddRange(Lines);
        Lines = merged;
        StartTime = previous.StartTime;
        Favorite |= previous.Favorite;
        if (Name == "") Name = previous.Name; // 只有前一段有名字時，沿用它的名字
    }
}

public class ShipRouteList
{
    /// <summary>最多保留幾段一般航線（我的最愛不算）。和原版相同是 30。</summary>
    public int MaxRoutesWithoutFavorites { get; set; } = 30;

    /// <summary>所有航線，舊 → 新。</summary>
    public List<ShipRoute> Routes { get; } = [];

    /// <summary>航線有變動時觸發（之後航線管理視窗用）。</summary>
    public event EventHandler? Changed;

    /// <summary>目前正在記錄的航線（最後一段且還沒結束）。沒有時是 null。</summary>
    public ShipRoute? Current
    {
        get
        {
            if (Routes.Count == 0) return null;
            ShipRoute last = Routes[^1];
            return last.Closed ? null : last;
        }
    }

    /// <summary>加一個點到目前的航線；沒有正在記錄的航線就開一段新的。</summary>
    public void AddPoint(WorldPoint p)
    {
        ShipRoute? route = Current;
        if (route is null)
        {
            route = new ShipRoute();
            Routes.Add(route);
            TrimOldRoutes();
        }
        route.AddPoint(p);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>結束目前的航線，下一個點會開新的一段。</summary>
    public void CloseCurrent()
    {
        ShipRoute? route = Current;
        if (route is null)
        {
            return;
        }
        route.Closed = true;
        // 只有一個點（或沒有點）的航線沒有意義，直接丟掉
        if (route.Lines.Sum(l => l.Count) < 2 && !route.Favorite)
        {
            Routes.Remove(route);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(ShipRoute route)
    {
        if (Routes.Remove(route))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear()
    {
        Routes.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 加入／取消我的最愛。透過這裡改，才會觸發 Changed（自動存檔），
    /// 取消我的最愛後一般航線超過上限時也會刪掉最舊的。
    /// </summary>
    public void SetFavorite(ShipRoute route, bool favorite)
    {
        if (route.Favorite == favorite) return;
        route.Favorite = favorite;
        TrimOldRoutes();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>改名。透過這裡改，才會觸發 Changed（自動存檔）。</summary>
    public void SetName(ShipRoute route, string name)
    {
        name = name.Trim();
        if (route.Name == name) return;
        route.Name = name;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>顯示／隱藏航線。透過這裡改，才會觸發 Changed（自動存檔、重畫地圖）。</summary>
    public void SetHidden(ShipRoute route, bool hidden)
    {
        if (route.Hidden == hidden) return;
        route.Hidden = hidden;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>全部顯示或全部隱藏。</summary>
    public void SetAllHidden(bool hidden)
    {
        bool changed = false;
        foreach (ShipRoute r in Routes)
        {
            if (r.Hidden != hidden)
            {
                r.Hidden = hidden;
                changed = true;
            }
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>清除我的最愛以外的航線。</summary>
    public void ClearExceptFavorites()
    {
        Routes.RemoveAll(r => !r.Favorite);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>換成讀檔讀進來的航線。</summary>
    public void ReplaceAll(IEnumerable<ShipRoute> routes)
    {
        Routes.Clear();
        Routes.AddRange(routes);
        TrimOldRoutes();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把一段已完成的航線加進清單（例如把規劃航線存起來）。放在目前記錄中的航線前面。</summary>
    public void AddFinished(ShipRoute route)
    {
        route.Closed = true;
        int index = Current is not null ? Routes.Count - 1 : Routes.Count;
        Routes.Insert(index, route);
        TrimOldRoutes();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把 route 和它前一段航線合併。</summary>
    public bool JoinWithPrevious(ShipRoute route)
    {
        int index = Routes.IndexOf(route);
        if (index <= 0)
        {
            return false;
        }
        ShipRoute previous = Routes[index - 1];
        bool highlight = previous.Highlight || route.Highlight;
        route.JoinPrevious(previous);
        route.Highlight = highlight;
        Routes.RemoveAt(index - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // 一般航線超過上限時，從最舊的開始刪（我的最愛不刪）
    private void TrimOldRoutes()
    {
        int normalCount = Routes.Count(r => !r.Favorite);
        int over = normalCount - MaxRoutesWithoutFavorites;
        for (int i = 0; i < Routes.Count && over > 0;)
        {
            if (!Routes[i].Favorite)
            {
                Routes.RemoveAt(i);
                over--;
            }
            else
            {
                i++;
            }
        }
    }
}
