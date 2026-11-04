// RoutePlanner.cs
// 規劃航線：從起點到終點找一條避開陸地的航線。
//
// 做法：
//   1. 把地圖當成方格（每格 = 地圖圖檔的一個像素，4096×2048 的地圖每格是 4×4 個遊戲座標）。
//   2. 用 A* 搜尋找最短的海上路徑（16 個方向都能走，地圖左右相連，可以繞地球）。
//      只有 8 個方向時，方格距離和真實距離最多差 8%，遇到小島時常常選錯從哪一邊繞；
//      加上「走兩格、斜一格」的 8 個方向後，誤差降到約 3%。
//      太靠近海岸的格子會「比較貴」，所以航線會和海岸保持一點距離，但窄的海峽還是過得去。
//   3. 找到的路徑是一格一格的鋸齒，最後拉直：能直線到達的點就直接連過去，
//      只在需要轉彎的地方留下轉折點，跟實際開船的走法比較像。
//   4. 像拉橡皮筋一樣收緊：每個轉折點往前後兩點的連線方向移，移到快碰到陸地為止。

using System;
using System.Collections.Generic;

namespace GVONavish.Core;

/// <summary>規劃好的航線。</summary>
public class PlannedRoute
{
    /// <summary>
    /// 轉折點（世界座標）。X 是「攤開」的座標：跨過地圖邊界時不折回，維持連續，畫圖時直接用。
    /// </summary>
    public List<Vec2> Waypoints { get; } = [];

    /// <summary>總距離（世界座標單位）。</summary>
    public double Length { get; set; }

    public WorldPoint Start { get; set; }
    public WorldPoint End { get; set; }

    /// <summary>起點、終點的名稱（港口名稱或座標文字），顯示用。</summary>
    public string StartName { get; set; } = "";
    public string EndName { get; set; } = "";
}

public class RoutePlanner
{
    private readonly SeaMap _map;

    // A* 用的陣列，重複使用；用「第幾次搜尋」的編號判斷資料是不是這一次的，不用每次清空
    private readonly float[] _g;
    private readonly int[] _parent;
    private readonly int[] _stamp;
    private readonly bool[] _closed;
    private int _run;

    /// <summary>離海岸幾格以內要加成本（越近越貴）。</summary>
    private static readonly float[] CoastPenalty = [0f, 3f, 1.5f, 0.6f, 0.2f];

    /// <summary>拉直航線時，直線要和海岸保持的距離（格）。窄海峽會自動放寬。</summary>
    private const int SmoothClearance = 3;

    public RoutePlanner(SeaMap map)
    {
        _map = map;
        int n = map.Width * map.Height;
        _g = new float[n];
        _parent = new int[n];
        _stamp = new int[n];
        _closed = new bool[n];
    }

    /// <summary>
    /// 規劃航線。找不到（例如起點在內陸湖）時回傳 null。
    /// </summary>
    public PlannedRoute? Plan(WorldPoint start, WorldPoint end)
    {
        (int sx, int sy) = _map.CellFromWorld(start.X, start.Y);
        (int ex, int ey) = _map.CellFromWorld(end.X, end.Y);

        // 港口通常畫在陸地邊上，起點、終點如果落在陸地，就找最近的海
        if (!SnapToSea(ref sx, ref sy) || !SnapToSea(ref ex, ref ey))
        {
            return null;
        }

        List<int>? cells = AStar(sy * _map.Width + sx, ey * _map.Width + ex);
        if (cells is null)
        {
            return null;
        }

        // 攤開 X：路徑跨過地圖邊界時讓 X 繼續增加或減少，不折回
        List<(double X, int Y)> pts = new(cells.Count);
        double ux = cells[0] % _map.Width;
        pts.Add((ux, cells[0] / _map.Width));
        for (int i = 1; i < cells.Count; i++)
        {
            int px = cells[i - 1] % _map.Width, cx = cells[i] % _map.Width;
            int d = cx - px;
            if (d > _map.Width / 2) d -= _map.Width;
            else if (d < -_map.Width / 2) d += _map.Width;
            ux += d;
            pts.Add((ux, cells[i] / _map.Width));
        }

        List<int> keep = Smooth(pts);

        // 拉直後的轉折點（格子座標），再收緊
        List<(double X, double Y)> corners = [(pts[0].X, pts[0].Y)];
        foreach (int k in keep) corners.Add((pts[k].X, pts[k].Y));
        Tighten(corners);

        PlannedRoute route = new() { Start = start, End = end };
        double cellW = (double)World.Width / _map.Width;
        double cellH = (double)World.Height / _map.Height;

        // 第一點用使用者輸入的起點（可能在陸地上，連一小段到海上），X 對齊到攤開後的座標系
        double startWorldX = (pts[0].X + 0.5) * cellW;
        double sxWorld = startWorldX + World.WrappedDeltaX((int)Math.Round(World.WrapX(startWorldX)), start.X);
        route.Waypoints.Add(new Vec2(sxWorld, start.Y));
        for (int k = 1; k < corners.Count; k++)
        {
            route.Waypoints.Add(new Vec2((corners[k].X + 0.5) * cellW, (corners[k].Y + 0.5) * cellH));
        }
        double lastX = route.Waypoints[^1].X;
        route.Waypoints.Add(new Vec2(lastX + World.WrappedDeltaX((int)Math.Round(World.WrapX(lastX)), end.X), end.Y));

        // 去掉重複的點、算總距離
        for (int i = route.Waypoints.Count - 1; i > 0; i--)
        {
            Vec2 a = route.Waypoints[i - 1], b = route.Waypoints[i];
            if (Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5)
            {
                route.Waypoints.RemoveAt(i);
            }
        }
        for (int i = 1; i < route.Waypoints.Count; i++)
        {
            Vec2 a = route.Waypoints[i - 1], b = route.Waypoints[i];
            route.Length += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        }
        return route;
    }

    // 找最近的海（離岸至少 1 格），最多找 60 格遠
    private bool SnapToSea(ref int x, ref int y)
    {
        if (_map.IsSeaCell(x, y))
        {
            return true;
        }
        for (int r = 1; r <= 60; r++)
        {
            int bestD = int.MaxValue, bx = 0, by = 0;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // 只看這一圈
                    int nx = _map.WrapCellX(x + dx), ny = y + dy;
                    if (!_map.IsSeaCell(nx, ny)) continue;
                    int d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; bx = nx; by = ny; }
                }
            }
            if (bestD != int.MaxValue)
            {
                x = bx;
                y = by;
                return true;
            }
        }
        return false;
    }

    private List<int>? AStar(int start, int goal)
    {
        int w = _map.Width, h = _map.Height;
        int gx = goal % w, gy = goal / w;
        _run++;
        if (_run == int.MaxValue)
        {
            Array.Clear(_stamp, 0, _stamp.Length);
            _run = 1;
        }

        PriorityQueue<int, float> open = new();
        Touch(start);
        _g[start] = 0;
        _parent[start] = -1;
        open.Enqueue(start, Heuristic(start % w, start / w, gx, gy));

        while (open.Count > 0)
        {
            int cur = open.Dequeue();
            if (_closed[cur])
            {
                continue; // 同一格可能排進去好幾次，只處理最早的那次
            }
            _closed[cur] = true;
            if (cur == goal)
            {
                List<int> path = [];
                for (int c = goal; c != -1; c = _parent[c]) path.Add(c);
                path.Reverse();
                return path;
            }

            int cx = cur % w, cy = cur / w;
            float gCur = _g[cur];
            for (int m = 0; m < Moves.Length; m++)
            {
                (int dx, int dy, float len) = Moves[m];
                int ny = cy + dy;
                if (ny < 0 || ny >= h) continue;
                int nx = cx + dx;
                if (nx < 0) nx += w; else if (nx >= w) nx -= w;
                int next = ny * w + nx;
                if (!_map.IsSea[next]) continue;

                // 不能從陸地的縫隙穿過去：斜走、跳兩格時，經過的格子也必須是海
                if (dx != 0 && dy != 0 && !PassIsClear(cx, cy, dx, dy)) continue;

                Touch(next);
                if (_closed[next]) continue;

                int coast = _map.CoastDistance[next];
                float penalty = coast < CoastPenalty.Length ? CoastPenalty[coast] : 0f;
                float g = gCur + len * (1f + penalty);
                if (g < _g[next])
                {
                    _g[next] = g;
                    _parent[next] = cur;
                    open.Enqueue(next, g + Heuristic(nx, ny, gx, gy));
                }
            }
        }
        return null;
    }

    // 第一次碰到這一格時，初始化它的資料
    private void Touch(int i)
    {
        if (_stamp[i] != _run)
        {
            _stamp[i] = _run;
            _g[i] = float.MaxValue;
            _closed[i] = false;
        }
    }

    // 16 個方向：上下左右、斜角、「走兩格斜一格」，以及每一步的長度
    private static readonly (int Dx, int Dy, float Len)[] Moves = BuildMoves();

    private static (int, int, float)[] BuildMoves()
    {
        List<(int, int, float)> list = [];
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int ax = Math.Abs(dx), ay = Math.Abs(dy);
                bool ok = (ax <= 1 && ay <= 1 && (ax + ay) > 0) || (ax == 2 && ay == 1) || (ax == 1 && ay == 2);
                if (ok) list.Add((dx, dy, (float)Math.Sqrt(dx * dx + dy * dy)));
            }
        }
        return [.. list];
    }

    // 斜走或跳兩格時，路上經過的格子都要是海（不能穿過陸地的縫隙）
    private bool PassIsClear(int cx, int cy, int dx, int dy)
    {
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        if (Math.Abs(dx) == 1 && Math.Abs(dy) == 1)
        {
            return SeaAt(cx + sx, cy) && SeaAt(cx, cy + sy);
        }
        if (Math.Abs(dx) == 2)
        {
            return SeaAt(cx + sx, cy) && SeaAt(cx + sx, cy + sy);
        }
        return SeaAt(cx, cy + sy) && SeaAt(cx + sx, cy + sy);
    }

    private bool SeaAt(int x, int y) => y >= 0 && y < _map.Height && _map.IsSea[y * _map.Width + _map.WrapCellX(x)];

    // 估計剩下的距離：沒有障礙物時，用這 16 個方向走過去的最短距離（左右相連取近的那邊）
    private float Heuristic(int x, int y, int gx, int gy)
    {
        int dx = Math.Abs(x - gx);
        if (dx > _map.Width / 2) dx = _map.Width - dx;
        int dy = Math.Abs(y - gy);
        int mx = Math.Max(dx, dy), mn = Math.Min(dx, dy);
        const float knight = 2.23606798f, diag = 1.41421356f;
        if (2 * mn <= mx)
        {
            return mn * knight + (mx - 2 * mn);            // 先跳兩格斜一格，剩下直走
        }
        return (mx - mn) * knight + (2 * mn - mx) * diag;  // 跳兩格斜一格 + 斜走
    }

    /// <summary>
    /// 拉直：把一格一格的鋸齒路徑，變成幾段直線。回傳要保留的點的索引（含最後一點）。
    ///
    /// 為什麼需要這樣做：在方格上，「先斜走再直走」和「先直走再斜走」步數一樣，
    /// 同樣最短的方格路徑有很多條，找到哪一條是隨機的；拉直後才會是真正的直線航線。
    ///
    /// 做法：
    ///   1. 從目前的轉折點，由「最遠的點」往回找第一個能直線到達的點（直線不碰陸地、離海岸至少 3 格）。
    ///      從遠處找起，所以不會因為中間有小島擋住一段，就提早留下多餘的轉折點。
    ///   2. 在窄海峽裡，離海岸 3 格的直線不存在，這時放寬要求，沿著原本的路徑一小段一小段拉直。
    ///   3. 最後再檢查一遍：如果拿掉某個轉折點，前後兩點還是能直線相連，就拿掉它。
    /// </summary>
    private List<int> Smooth(List<(double X, int Y)> pts)
    {
        int n = pts.Count;
        List<int> keep = [0];
        int anchor = 0;
        while (anchor < n - 1)
        {
            int next = FarthestVisible(pts, anchor);
            keep.Add(next);
            anchor = next;
        }

        // 拿掉多餘的轉折點
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int k = 1; k < keep.Count - 1; k++)
            {
                if (LineIsClear(P(pts[keep[k - 1]]), P(pts[keep[k + 1]]), SmoothClearance))
                {
                    keep.RemoveAt(k);
                    changed = true;
                    k--;
                }
            }
        }

        keep.RemoveAt(0); // 起點另外處理
        return keep;
    }

    private static (double X, double Y) P((double X, int Y) q) => (q.X, q.Y);

    /// <summary>從 anchor 出發，能直線到達的最遠的點。</summary>
    private int FarthestVisible(List<(double X, int Y)> pts, int anchor)
    {
        int n = pts.Count;

        // 1. 開闊海面：從最遠的點往回找（點很多時跳著找，找到後再往前補幾個）
        int span = n - 1 - anchor;
        int stride = Math.Max(1, span / 256);
        for (int j = n - 1; j > anchor + 1; j -= stride)
        {
            if (LineIsClear(P(pts[anchor]), P(pts[j]), SmoothClearance))
            {
                int best = j;
                for (int k = j + 1; k < n && k < j + stride; k++)
                {
                    if (LineIsClear(P(pts[anchor]), P(pts[k]), SmoothClearance)) best = k;
                }
                return best;
            }
        }

        // 2. 窄海峽：沿著原本的路徑往前，要求的離岸距離放寬到「原本路徑這一段最靠近海岸的距離」
        int bestNarrow = anchor + 1;
        int minCoastOnPath = int.MaxValue;
        for (int j = anchor + 1; j < n && j - bestNarrow <= 40; j++)
        {
            int c = _map.CoastDistance[pts[j].Y * _map.Width + _map.WrapCellX((int)pts[j].X)];
            if (c < minCoastOnPath) minCoastOnPath = c;
            int need = Math.Min(SmoothClearance, minCoastOnPath);
            if (LineIsClear(P(pts[anchor]), P(pts[j]), need))
            {
                bestNarrow = j;
            }
        }
        return bestNarrow;
    }

    /// <summary>
    /// 收緊（像拉橡皮筋）：每個轉折點往前後兩點的連線方向移動，移到快碰到陸地為止；
    /// 移到前後兩點可以直接相連時，就拿掉這個轉折點。重複幾輪，直到幾乎沒有變化。
    /// 每一段都不能比原本更貼近海岸（原本在窄海峽裡的段落，維持原本的離岸距離）。
    /// </summary>
    private void Tighten(List<(double X, double Y)> p)
    {
        for (int round = 0; round < 30; round++)
        {
            bool moved = false;
            for (int k = 1; k < p.Count - 1; k++)
            {
                var a = p[k - 1];
                var w = p[k];
                var b = p[k + 1];

                int needA = SegmentClearance(a, w);
                int needB = SegmentClearance(w, b);
                int needAB = Math.Min(needA, needB);

                // 前後兩點已經能直接相連：這個轉折點多餘
                if (LineIsClear(a, b, needAB))
                {
                    p.RemoveAt(k);
                    k--;
                    moved = true;
                    continue;
                }

                // 目標：前後兩點連線上，離這個轉折點最近的位置
                double abx = b.X - a.X, aby = b.Y - a.Y;
                double len2 = abx * abx + aby * aby;
                double t = len2 > 0 ? ((w.X - a.X) * abx + (w.Y - a.Y) * aby) / len2 : 0;
                t = Math.Clamp(t, 0, 1);
                var target = (X: a.X + abx * t, Y: a.Y + aby * t);

                // 二分法：往目標移動，能移多遠就移多遠
                double lo = 0, hi = 1;
                for (int it = 0; it < 10; it++)
                {
                    double mid = (lo + hi) / 2;
                    var c = (X: w.X + (target.X - w.X) * mid, Y: w.Y + (target.Y - w.Y) * mid);
                    if (LineIsClear(a, c, needA) && LineIsClear(c, b, needB)) lo = mid;
                    else hi = mid;
                }
                double dist = Math.Sqrt((target.X - w.X) * (target.X - w.X) + (target.Y - w.Y) * (target.Y - w.Y)) * lo;
                if (dist > 0.25)
                {
                    p[k] = (w.X + (target.X - w.X) * lo, w.Y + (target.Y - w.Y) * lo);
                    moved = true;
                }
            }
            if (!moved) break;
        }
    }

    // 這一段直線目前能保持的離岸距離（3、2、1 或 0 格）
    private int SegmentClearance((double X, double Y) a, (double X, double Y) b)
    {
        for (int need = SmoothClearance; need >= 1; need--)
        {
            if (LineIsClear(a, b, need)) return need;
        }
        return 0;
    }

    /// <summary>
    /// 直線經過的每一格都是海，而且離海岸至少 need 格。
    /// 兩端附近（need 格以內）只要求是海：端點本身可能就在海峽或港口邊，離岸本來就近。
    /// </summary>
    /// <remarks>
    /// 用格子走訪（Amanatides–Woo）把線段真正碰到的每一格都查過。
    /// 以前是每半格取一個點，斜線可能從兩個取樣點中間擦過一格陸地的角落。
    /// 線剛好穿過格子的角時，角旁邊兩格都算碰到（和 A* 不准從斜向縫隙穿過去一致）。
    /// 座標單位是格子，整數是格子中心（格子 i 的範圍是 i-0.5 ~ i+0.5），X 是攤開的。
    /// </remarks>
    private bool LineIsClear((double X, double Y) a, (double X, double Y) b, int need)
    {
        // 平移半格，讓「格子 i 的範圍」變成 i ~ i+1，Floor 就是格子編號
        double ux = a.X + 0.5, uy = a.Y + 0.5;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Max(Math.Abs(dx), Math.Abs(dy)); // 和以前一樣用較長的一邊算「離端點幾格」

        int x = (int)Math.Floor(ux), y = (int)Math.Floor(uy);
        int stepX = Math.Sign(dx), stepY = Math.Sign(dy);
        // tMax：走到下一條直／橫格線時的 t（0 ~ 1）；tDelta：每跨一格 t 增加多少
        double tDeltaX = dx != 0 ? 1.0 / Math.Abs(dx) : double.PositiveInfinity;
        double tDeltaY = dy != 0 ? 1.0 / Math.Abs(dy) : double.PositiveInfinity;
        double tMaxX = dx > 0 ? (x + 1 - ux) / dx : dx < 0 ? (ux - x) / -dx : double.PositiveInfinity;
        double tMaxY = dy > 0 ? (y + 1 - uy) / dy : dy < 0 ? (uy - y) / -dy : double.PositiveInfinity;

        double tEnter = 0;
        while (true)
        {
            double tExit = Math.Min(Math.Min(tMaxX, tMaxY), 1.0);
            if (!CellIsClear(x, y, tEnter, tExit, length, need)) return false;
            if (tExit >= 1.0) return true;

            if (Math.Abs(tMaxX - tMaxY) < 1e-9)
            {
                // 剛好穿過格子的角：角旁邊的兩格也算碰到
                if (!CellIsClear(x + stepX, y, tExit, tExit, length, need)) return false;
                if (!CellIsClear(x, y + stepY, tExit, tExit, length, need)) return false;
                x += stepX;
                y += stepY;
                tMaxX += tDeltaX;
                tMaxY += tDeltaY;
            }
            else if (tMaxX < tMaxY)
            {
                x += stepX;
                tMaxX += tDeltaX;
            }
            else
            {
                y += stepY;
                tMaxY += tDeltaY;
            }
            tEnter = tExit;
        }
    }

    // 線段在 t = tEnter ~ tExit 經過格子 (x, y)：這格必須是海；離兩端超過 need 格的地方，還要離岸至少 need 格
    private bool CellIsClear(int x, int y, double tEnter, double tExit, double length, int need)
    {
        if (y < 0 || y >= _map.Height) return false;
        int i = y * _map.Width + _map.WrapCellX(x);
        if (!_map.IsSea[i]) return false;
        bool nearEnd = tEnter * length <= need || (1.0 - tExit) * length <= need;
        return nearEnd || _map.CoastDistance[i] >= need;
    }
}
