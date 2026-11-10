// MapView.cs
// 地圖顯示元件：畫世界地圖和船的位置，支援拖曳、滾輪縮放、跟隨船隻。
// 取代原版的 GVORenderer（OpenGL），改用 GDI+ 繪製。
//
// 座標系統：
//   世界座標  0~16384 × 0~8192（遊戲的測量座標）
//   地圖圖檔  例如 4096 × 2048 像素（任何 2:1 的圖都可以）
//   畫面      視窗裡的像素
// 地圖左右相連，所以畫的時候會左右重複貼，往東一直拖會繞回來。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using GVONavish.Core;

namespace GVONavish.UI;

/// <summary>船速框放在哪個角落。</summary>
public enum ScreenCorner
{
    TopRight,
    BottomRight,
    TopLeft,
    BottomLeft,
}

/// <summary>雙擊地圖的資訊。</summary>
public class MapClickEventArgs : EventArgs
{
    public WorldPoint World { get; }
    public Keys Modifiers { get; }
    public bool OnShip { get; }
    public bool OnMarker { get; }

    public MapClickEventArgs(WorldPoint world, Keys modifiers, bool onShip, bool onMarker)
    {
        World = world;
        Modifiers = modifiers;
        OnShip = onShip;
        OnMarker = onMarker;
    }
}

// 外部可以設定的公開屬性（有 public set）都標了 [DesignerSerializationVisibility(Hidden)]：
// 這個控制項是用程式碼建立的，不經過表單設計工具，屬性不需要存進設計工具產生的程式碼。
// .NET 9 起不標會出現 WFO1000 錯誤，Publish 會失敗。以後新增「有 public set」的屬性也要記得加。
// 唯讀的屬性（只有 get，或 private set，例如 ShipPosition、HasMap）設計工具本來就不會存，不用標。
public class MapView : Control
{
    public const double MinScale = 0.125;   // 12.5%（和原版相同）
    public const double MaxScale = 4.0;     // 400%
    public const double ScaleStep = 0.125;  // 每次 12.5%

    private static readonly Color BackgroundColor = Color.FromArgb(51, 51, 77); // 原版的背景色

    private Bitmap? _map;                     // 地圖原圖（已轉成最快的像素格式）
    private readonly Dictionary<double, Bitmap> _scaledCache = [];
    private double _scale = 1.0;
    private double _focusX;                   // 畫面中心對到的世界座標
    private double _focusY;
    private bool _followShip = true;

    private bool _dragging;
    private bool _dragStarted;
    private Point _dragLast;
    private Point _dragStart;
    private const int DragThreshold = 3;      // 移動超過幾像素才算拖曳（避免單擊就關掉跟隨）

    /// <summary>船的位置。null 表示還不知道。</summary>
    public WorldPoint? ShipPosition { get; private set; }

    /// <summary>船的記號圖示（通常是遊戲的小圖示）。null 時畫一個圓點。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image? ShipIcon { get; set; }

    /// <summary>要畫的航線清單。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ShipRouteList? Routes { get; set; }

    /// <summary>船的航向（長度 1 的向量）。0 向量表示還不知道。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Vec2 Heading { get; set; }

    /// <summary>航向角度（0 = 正北，順時針）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double? HeadingDegrees { get; set; }

    /// <summary>船速（節）。null 表示還不知道。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double? SpeedKnots { get; set; }

    /// <summary>是否顯示航向線（從船往前延伸的線）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowHeadingLine { get; set; } = true;

    /// <summary>是否在右上角顯示船速。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSpeed { get; set; } = true;

    /// <summary>是否顯示航跡。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowRoutes { get; set; } = true;

    /// <summary>船速框放在哪個角落。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ScreenCorner SpeedCorner { get; set; } = ScreenCorner.TopRight;

    /// <summary>規劃的航線（綠色虛線），可以有好幾段接在一起。null 或空清單表示沒有。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<PlannedRoute>? PlannedLegs { get; set; }

    /// <summary>規劃航線上逆風的航段（橘色實線，疊在綠色虛線上）。null 或空清單表示沒有。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<(Vec2 From, Vec2 To)>? HeadwindRuns { get; set; }

    /// <summary>雙擊地圖留下的座標標籤位置。null 表示沒有。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public WorldPoint? Marker { get; set; }

    /// <summary>座標標籤的文字。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string MarkerText { get; set; } = "";

    /// <summary>在地圖上雙擊（左鍵）。附帶按下的修飾鍵，以及是不是點在船上。</summary>
    public event EventHandler<MapClickEventArgs>? MapDoubleClicked;

    /// <summary>滑鼠在地圖上移動（世界座標）；離開地圖時是 null。</summary>
    public event EventHandler<WorldPoint?>? MouseWorldMoved;

    /// <summary>最後一次按右鍵的位置（世界座標），給右鍵選單「規劃航線到這裡」用。</summary>
    public WorldPoint? LastRightClickWorld { get; private set; }

    /// <summary>
    /// 目前讀不到座標（測量結束、進港…）。船速和航向線改成灰色，表示是舊資料。
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Stale { get; set; }

    // 航線顏色（和原版相同）
    private static readonly Color RouteOldColor = Color.FromArgb(128, 255, 255, 255);     // 舊航線：半透明白
    private static readonly Color RouteLatestColor = Color.White;                         // 最新航線：白
    private static readonly Color RouteFavoriteColor = Color.FromArgb(191, 255, 255, 0);  // 我的最愛：黃
    // 選取中：橘紅、粗一點、外圍加深色描邊。地圖上已經用了白、黃、洋紅（航向線）、亮綠（規劃航線），
    // 橘紅和它們都分得開，在藍色海面和陸地上也都明顯
    private static readonly Color RouteHighlightColor = Color.FromArgb(255, 255, 100, 30);
    private static readonly Color RouteHighlightShadow = Color.FromArgb(170, 0, 0, 0);
    private static readonly Color HeadingLineColor = Color.Magenta;                       // 航向線：洋紅
    private static readonly Color StaleColor = Color.FromArgb(150, 160, 160, 160);       // 舊資料：半透明灰
    private static readonly Color PlannedColor = Color.FromArgb(0, 255, 140);             // 規劃航線：亮綠
    private static readonly Color HeadwindColor = Color.FromArgb(255, 140, 0);            // 規劃航線逆風的航段：橘
    private static readonly Color MarkerColor = Color.FromArgb(255, 220, 80);             // 座標標籤：黃

    /// <summary>比例或位置改變時觸發（用來更新標題列）。</summary>
    public event EventHandler? ViewChanged;

    /// <summary>使用者拖曳地圖或雙擊，造成「跟隨船隻」開關改變時觸發。</summary>
    public event EventHandler? FollowShipChanged;

    public MapView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        BackColor = BackgroundColor;
        TabStop = true;
    }

    /// <summary>顯示比例（1.0 = 100%）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double ViewScale
    {
        get => _scale;
        set
        {
            double s = ClampScale(value);
            if (s == _scale)
            {
                return;
            }
            _scale = s;
            Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>地圖是否跟著船移動。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FollowShip
    {
        get => _followShip;
        set
        {
            if (_followShip == value)
            {
                return;
            }
            _followShip = value;
            if (_followShip && ShipPosition.HasValue)
            {
                SetFocus(ShipPosition.Value.X, ShipPosition.Value.Y);
            }
            FollowShipChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasMap => _map is not null;

    /// <summary>
    /// 設定地圖圖檔。傳進來的 Bitmap 會被複製，呼叫端可以自行 Dispose 原圖。
    /// </summary>
    public void SetMap(Bitmap? source)
    {
        ClearCache();
        _map?.Dispose();
        _map = null;

        if (source is not null)
        {
            // 轉成 32bppPArgb：GDI+ 畫這種格式最快
            _map = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
            using Graphics g = Graphics.FromImage(_map);
            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        }
        Invalidate();
    }

    /// <summary>更新船的位置。若開啟跟隨，地圖會以船為中心。</summary>
    public void SetShipPosition(WorldPoint p)
    {
        ShipPosition = p;
        if (_followShip)
        {
            SetFocus(p.X, p.Y);
        }
        Invalidate();
    }

    /// <summary>把畫面中心移到指定的世界座標。</summary>
    public void SetFocus(double worldX, double worldY)
    {
        _focusX = World.WrapX(worldX);
        _focusY = Math.Clamp(worldY, 0, World.Height);
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => ZoomAt(ViewScale + ScaleStep, null);
    public void ZoomOut() => ZoomAt(ViewScale - ScaleStep, null);
    public void ResetZoom() => ZoomAt(1.0, null);

    /// <summary>
    /// 改變比例。anchor 是畫面上的一點，縮放後這一點下面的地點維持不動；null 表示以畫面中心縮放。
    /// </summary>
    private void ZoomAt(double newScale, Point? anchor)
    {
        newScale = ClampScale(newScale);
        if (newScale == _scale || _map is null)
        {
            ViewScale = newScale;
            return;
        }

        // 跟隨船隻時一律以船（畫面中心）為準，否則以滑鼠位置為準
        if (anchor.HasValue && !_followShip)
        {
            double cx = ClientSize.Width / 2.0;
            double cy = ClientSize.Height / 2.0;
            double ax = anchor.Value.X - cx;
            double ay = anchor.Value.Y - cy;

            // 縮放前，滑鼠下的世界座標
            double wx = _focusX + ax / PixelsPerWorldX(_scale);
            double wy = _focusY + ay / PixelsPerWorldY(_scale);

            _scale = newScale;

            // 縮放後，讓同一個世界座標仍在滑鼠下
            SetFocus(wx - ax / PixelsPerWorldX(_scale), wy - ay / PixelsPerWorldY(_scale));
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ViewScale = newScale;
        }
    }

    private static double ClampScale(double s)
    {
        s = Math.Round(s / ScaleStep) * ScaleStep; // 對齊 12.5% 的格子，避免浮點誤差累積
        return Math.Clamp(s, MinScale, MaxScale);
    }

    // 一個世界座標單位 = 畫面上幾個像素
    private double PixelsPerWorldX(double scale) => _map is null ? 1 : _map.Width * scale / World.Width;
    private double PixelsPerWorldY(double scale) => _map is null ? 1 : _map.Height * scale / World.Height;

    #region 繪圖

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackgroundColor);

        if (_map is null)
        {
            TextRenderer.DrawText(g, "沒有地圖圖檔。請按右鍵選「選擇地圖圖檔…」。", Font,
                ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        double mapW = _map.Width * _scale;   // 放大縮小後，一整張地圖在畫面上的大小
        double mapH = _map.Height * _scale;
        double kx = PixelsPerWorldX(_scale);
        double ky = PixelsPerWorldY(_scale);

        var view = CurrentView();
        double left = view.Left, top = view.Top;

        // 地圖左右重複貼滿畫面
        for (double x = left; x < ClientSize.Width; x += mapW)
        {
            DrawMapTile(g, x, top, mapW, mapH);
        }

        // 航線
        if (ShowRoutes && Routes is not null && Routes.Routes.Count > 0)
        {
            DrawRoutes(g, view);
        }

        // 航向線（從船往前延伸 8192 格，和原版相同）
        if (ShowHeadingLine && ShipPosition.HasValue && !Heading.IsZero)
        {
            const double lineLength = World.Height;
            WorldPoint s = ShipPosition.Value;
            Vec2[] pts =
            [
                new(s.X, s.Y),
                new(s.X + Heading.X * lineLength, s.Y + Heading.Y * lineLength),
            ];
            using Pen pen = new(Stale ? StaleColor : HeadingLineColor, LineWidth);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawWrappedPolyline(g, pen, pts, view);
        }

        // 規劃航線
        if (PlannedLegs is not null && PlannedLegs.Count > 0)
        {
            DrawPlannedRoute(g, view);
        }

        // 雙擊留下的座標標籤
        if (Marker.HasValue)
        {
            DrawMarker(g, view);
        }

        // 船（同樣左右重複，地圖縮得很小、畫面裡有好幾張地圖時每張都畫）
        if (ShipPosition.HasValue)
        {
            for (double x = left; x < ClientSize.Width; x += mapW)
            {
                double sx = x + ShipPosition.Value.X * kx;
                double sy = top + ShipPosition.Value.Y * ky;
                DrawShip(g, (float)sx, (float)sy);
            }
        }

        // 右上角的船速
        if (ShowSpeed && SpeedKnots.HasValue)
        {
            DrawSpeed(g);
        }
    }

    /// <summary>目前畫面的換算資料（第一張地圖的左上角在畫面上的位置等）。</summary>
    private ViewTransform CurrentView()
    {
        double mapW = (_map?.Width ?? World.Width) * _scale;
        double kx = PixelsPerWorldX(_scale);
        double ky = PixelsPerWorldY(_scale);
        double left = ClientSize.Width / 2.0 - _focusX * kx;
        double top = ClientSize.Height / 2.0 - _focusY * ky;
        while (left > 0) left -= mapW;
        while (left + mapW <= 0) left += mapW;
        return new ViewTransform(left, top, mapW, kx, ky, ClientSize.Width);
    }

    /// <summary>世界座標 → 畫面座標的換算資料。</summary>
    private readonly struct ViewTransform
    {
        public readonly double Left;     // 第一張地圖的左邊（畫面座標，≤ 0）
        public readonly double Top;
        public readonly double MapWidth; // 一整張地圖在畫面上的寬度
        public readonly double Kx;       // 一個世界座標 = 幾個畫面像素
        public readonly double Ky;
        public readonly int ViewWidth;

        public ViewTransform(double left, double top, double mapWidth, double kx, double ky, int viewWidth)
        {
            Left = left;
            Top = top;
            MapWidth = mapWidth;
            Kx = kx;
            Ky = ky;
            ViewWidth = viewWidth;
        }
    }

    // 線寬：100% 以下是 1 像素，放大時跟著變粗（和原版相同）
    private float LineWidth => (float)Math.Max(1.0, _scale);

    private void DrawRoutes(Graphics g, ViewTransform view)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        List<ShipRoute> routes = Routes!.Routes;
        ShipRoute latest = routes[^1];
        float width = LineWidth;
        float highlightWidth = width * 2f;

        // 畫的順序（和原版相同）：一般航線 → 我的最愛 → 選取中的，後畫的在上面
        foreach (ShipRoute r in routes)
        {
            if (r.Favorite || r.Highlight || r.Hidden) continue;
            DrawRoute(g, r, r == latest ? RouteLatestColor : RouteOldColor, width, view);
        }
        foreach (ShipRoute r in routes)
        {
            if (!r.Favorite || r.Highlight || r.Hidden) continue;
            DrawRoute(g, r, r == latest ? Color.Yellow : RouteFavoriteColor, width, view);
        }
        foreach (ShipRoute r in routes)
        {
            if (!r.Highlight) continue; // 選取中的航線就算隱藏也畫出來，才看得到選的是哪一段
            // 不分一般航線或我的最愛，選取中一律用同一個顏色，一眼就看得出是哪一段
            DrawRoute(g, r, RouteHighlightShadow, highlightWidth + 3f, view);
            DrawRoute(g, r, RouteHighlightColor, highlightWidth, view);
        }
    }

    private void DrawRoute(Graphics g, ShipRoute route, Color color, float width, ViewTransform view)
    {
        using Pen pen = new(color, width) { LineJoin = LineJoin.Round };
        foreach (List<WorldPoint> line in route.Lines)
        {
            if (line.Count < 2)
            {
                continue; // 一個點畫不出線
            }

            // 把 X 座標「攤開」：跨過地圖右邊時不折回 0，而是繼續往右加（例如 16380 → 16390），
            // 這樣整條線是連續的，再交給 DrawWrappedPolyline 左右重複畫。
            var pts = new Vec2[line.Count];
            double x = line[0].X;
            pts[0] = new Vec2(x, line[0].Y);
            for (int i = 1; i < line.Count; i++)
            {
                x += World.WrappedDeltaX(line[i - 1].X, line[i].X);
                pts[i] = new Vec2(x, line[i].Y);
            }
            DrawWrappedPolyline(g, pen, pts, view);
        }
    }

    /// <summary>
    /// 畫一條世界座標的折線（X 可以超出 0~16384），地圖左右重複的每一份都畫，只畫看得到的。
    /// </summary>
    private void DrawWrappedPolyline(Graphics g, Pen pen, Vec2[] pts, ViewTransform view)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (Vec2 p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        // 上下都在畫面外就不用畫
        if (view.Top + maxY * view.Ky < -pen.Width || view.Top + minY * view.Ky > ClientSize.Height + pen.Width)
        {
            return;
        }

        // 算出要畫第幾份到第幾份（第 n 份往右偏移 n 張地圖寬）
        int nFrom = (int)Math.Floor((-pen.Width - view.Left - maxX * view.Kx) / view.MapWidth);
        int nTo = (int)Math.Ceiling((view.ViewWidth + pen.Width - view.Left - minX * view.Kx) / view.MapWidth);

        var screen = new PointF[pts.Length];
        for (int n = nFrom; n <= nTo; n++)
        {
            double offsetX = view.Left + n * view.MapWidth;
            double lineLeft = offsetX + minX * view.Kx;
            double lineRight = offsetX + maxX * view.Kx;
            if (lineRight < -pen.Width || lineLeft > view.ViewWidth + pen.Width)
            {
                continue;
            }
            for (int i = 0; i < pts.Length; i++)
            {
                screen[i] = new PointF((float)(offsetX + pts[i].X * view.Kx), (float)(view.Top + pts[i].Y * view.Ky));
            }
            g.DrawLines(pen, screen);
        }
    }

    private void DrawPlannedRoute(Graphics g, ViewTransform view)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using Pen shadow = new(Color.FromArgb(160, 0, 0, 0), LineWidth + 3f) { LineJoin = LineJoin.Round };
        using Pen pen = new(PlannedColor, LineWidth + 1f) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round };
        using SolidBrush dot = new(PlannedColor);
        using Pen ring = new(PlannedColor, 2f);

        foreach (PlannedRoute leg in PlannedLegs!)
        {
            if (leg.Waypoints.Count < 2) continue;
            Vec2[] pts = [.. leg.Waypoints];

            // 先畫一圈深色外框，在任何底色上都看得清楚
            DrawWrappedPolyline(g, shadow, pts, view);
            DrawWrappedPolyline(g, pen, pts, view);
        }

        // 逆風的航段：用橘色實線蓋在上面（在轉折點的圓點下面）
        if (HeadwindRuns is not null)
        {
            using Pen headwind = new(HeadwindColor, LineWidth + 1f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            foreach ((Vec2 from, Vec2 to) in HeadwindRuns)
            {
                DrawWrappedPolyline(g, headwind, [from, to], view);
            }
        }

        foreach (PlannedRoute leg in PlannedLegs!)
        {
            if (leg.Waypoints.Count < 2) continue;
            Vec2[] pts = [.. leg.Waypoints];

            // 轉折點畫小圓點，每一段的終點畫大一點的圈
            for (int i = 1; i < pts.Length; i++)
            {
                bool isEnd = i == pts.Length - 1;
                float r = isEnd ? 6f : 2.5f;
                foreach (PointF p in ScreenCopies(pts[i], view))
                {
                    if (isEnd) g.DrawEllipse(ring, p.X - r, p.Y - r, r * 2, r * 2);
                    else g.FillEllipse(dot, p.X - r, p.Y - r, r * 2, r * 2);
                }
            }
        }
    }

    /// <summary>座標標籤：在點的位置畫十字，旁邊用一條線拉出文字框（和 GVONavi 類似）。</summary>
    private void DrawMarker(Graphics g, ViewTransform view)
    {
        WorldPoint m = Marker!.Value;
        Size size = TextRenderer.MeasureText(g, MarkerText, Font);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using Pen pen = new(MarkerColor, 1.5f);
        using SolidBrush bg = new(Color.FromArgb(240, 255, 255, 225));
        using Pen border = new(Color.FromArgb(120, 100, 60), 1f);

        foreach (PointF p in ScreenCopies(new Vec2(m.X, m.Y), view))
        {
            // 十字
            g.DrawLine(pen, p.X - 6, p.Y, p.X + 6, p.Y);
            g.DrawLine(pen, p.X, p.Y - 6, p.X, p.Y + 6);

            // 文字框放在左下方；太靠邊時換到另一邊
            float w = size.Width + 6, h = size.Height + 2;
            float bx = p.X - 50 - w;
            float by = p.Y + 30;
            if (bx < 4) bx = p.X + 50;
            if (by + h + 4 > ClientSize.Height) by = p.Y - 30 - h;
            RectangleF box = new(bx, by, w, h);
            float lx = bx < p.X ? box.Right : box.Left;
            float ly = box.Top + box.Height / 2;
            g.DrawLine(pen, p.X, p.Y, lx, ly);
            g.FillRectangle(bg, box);
            g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
            TextRenderer.DrawText(g, MarkerText, Font, new Point((int)box.X + 3, (int)box.Y + 1), Color.Black);
        }
    }

    private bool _busy;

    /// <summary>
    /// 正在背景計算（例如規劃航線）。計算期間滑鼠游標固定顯示沙漏，拖曳、點擊都不會把它改掉。
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Busy
    {
        get => _busy;
        set
        {
            _busy = value;
            UpdateCursor();
        }
    }

    // 游標：計算中 → 沙漏；拖曳中 → 四向箭頭；其他 → 一般箭頭
    // （設定 Cursor 時，如果滑鼠正在地圖上，Windows 會馬上換掉游標，不用等滑鼠移動）
    private void UpdateCursor()
    {
        Cursor = _busy ? Cursors.WaitCursor : (_dragStarted ? Cursors.SizeAll : Cursors.Default);
    }

    /// <summary>畫面上這一點是不是點在船上（12 像素以內）。</summary>
    public bool IsOnShip(Point screen)
    {
        if (!ShipPosition.HasValue || _map is null) return false;
        ViewTransform view = CurrentView();
        foreach (PointF p in ScreenCopies(new Vec2(ShipPosition.Value.X, ShipPosition.Value.Y), view))
        {
            double dx = p.X - screen.X, dy = p.Y - screen.Y;
            if (dx * dx + dy * dy <= 12 * 12) return true;
        }
        return false;
    }

    /// <summary>畫面上的這個位置，是不是在座標標籤的十字上（附近 12 像素內；文字框不算）。</summary>
    public bool IsOnMarker(Point screen)
    {
        if (!Marker.HasValue || _map is null) return false;
        ViewTransform view = CurrentView();
        foreach (PointF p in ScreenCopies(new Vec2(Marker.Value.X, Marker.Value.Y), view))
        {
            double dx = p.X - screen.X, dy = p.Y - screen.Y;
            if (dx * dx + dy * dy <= 12 * 12) return true;
        }
        return false;
    }

    // 一個世界座標點在畫面上的所有位置（地圖左右重複，可能出現好幾次）
    private IEnumerable<PointF> ScreenCopies(Vec2 p, ViewTransform view)
    {
        double y = view.Top + p.Y * view.Ky;
        if (y < -10 || y > ClientSize.Height + 10) yield break;
        double baseX = view.Left + p.X * view.Kx;
        int nFrom = (int)Math.Floor((-10 - baseX) / view.MapWidth);
        int nTo = (int)Math.Ceiling((view.ViewWidth + 10 - baseX) / view.MapWidth);
        for (int n = nFrom; n <= nTo; n++)
        {
            double x = baseX + n * view.MapWidth;
            if (x >= -10 && x <= view.ViewWidth + 10) yield return new PointF((float)x, (float)y);
        }
    }

    /// <summary>畫面上的一點 → 世界座標。</summary>
    public WorldPoint WorldFromScreen(Point p)
    {
        double wx = _focusX + (p.X - ClientSize.Width / 2.0) / PixelsPerWorldX(_scale);
        double wy = _focusY + (p.Y - ClientSize.Height / 2.0) / PixelsPerWorldY(_scale);
        int x = (int)Math.Floor(World.WrapX(wx)) % World.Width;
        int y = Math.Clamp((int)Math.Floor(wy), 0, World.Height - 1);
        return new WorldPoint(x, y);
    }

    private void DrawSpeed(Graphics g)
    {
        string text = $"{SpeedKnots!.Value:0.00} kt";
        if (HeadingDegrees.HasValue)
        {
            text += $"　航向 {HeadingDegrees.Value:0}°";
        }

        Size size = TextRenderer.MeasureText(g, text, Font);
        const int margin = 6;
        int boxW = size.Width + 8;
        int boxH = size.Height + 4;
        bool right = SpeedCorner == ScreenCorner.TopRight || SpeedCorner == ScreenCorner.BottomRight;
        bool bottom = SpeedCorner == ScreenCorner.BottomRight || SpeedCorner == ScreenCorner.BottomLeft;
        int x = right ? ClientSize.Width - boxW - margin : margin;
        int y = bottom ? ClientSize.Height - boxH - margin : margin;
        Rectangle box = new(x, y, boxW, boxH);
        // 背景用不透明的深色，底下的地名不會透出來，灰字也看得清楚
        using (SolidBrush bg = new(Color.FromArgb(25, 25, 35)))
        {
            g.FillRectangle(bg, box);
        }
        TextRenderer.DrawText(g, text, Font, new Point(box.X + 4, box.Y + 2), Stale ? Color.FromArgb(150, 150, 150) : Color.White);
    }

    private void DrawMapTile(Graphics g, double tileX, double tileY, double tileW, double tileH)
    {
        // 這一張地圖和畫面重疊的範圍
        double visL = Math.Max(0, tileX);
        double visT = Math.Max(0, tileY);
        double visR = Math.Min(ClientSize.Width, tileX + tileW);
        double visB = Math.Min(ClientSize.Height, tileY + tileH);
        if (visR <= visL || visB <= visT)
        {
            return;
        }

        if (_scale <= 1.0)
        {
            // 縮小：用預先縮好的圖（畫質好），1:1 複製可見的部分，速度快
            Bitmap scaled = GetScaledMap();
            int sx = (int)Math.Floor(visL - tileX);
            int sy = (int)Math.Floor(visT - tileY);
            int dx = (int)Math.Floor(visL);
            int dy = (int)Math.Floor(visT);
            int w = Math.Min((int)Math.Ceiling(visR) - dx, scaled.Width - sx);
            int h = Math.Min((int)Math.Ceiling(visB) - dy, scaled.Height - sy);
            if (w <= 0 || h <= 0)
            {
                return;
            }
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(scaled, new Rectangle(dx, dy, w, h), new Rectangle(sx, sy, w, h), GraphicsUnit.Pixel);
        }
        else
        {
            // 放大：直接從原圖取可見的那一小塊放大，不做模糊處理（和原版一樣是方格狀）
            RectangleF src = new(
                (float)((visL - tileX) / _scale),
                (float)((visT - tileY) / _scale),
                (float)((visR - visL) / _scale),
                (float)((visB - visT) / _scale));
            RectangleF dest = new((float)visL, (float)visT, (float)(visR - visL), (float)(visB - visT));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_map!, dest, src, GraphicsUnit.Pixel);
        }
    }

    private void DrawShip(Graphics g, float x, float y)
    {
        if (x < -20 || y < -20 || x > ClientSize.Width + 20 || y > ClientSize.Height + 20)
        {
            return;
        }

        if (ShipIcon is not null)
        {
            const float size = 16f; // 和原版一樣固定 16 像素，不隨縮放改變
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(ShipIcon, x - size / 2, y - size / 2, size, size);
        }
        else
        {
            const float r = 6f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush fill = new(Color.Red))
            using (Pen pen = new(Color.White, 2f))
            {
                g.FillEllipse(fill, x - r, y - r, r * 2, r * 2);
                g.DrawEllipse(pen, x - r, y - r, r * 2, r * 2);
            }
            g.SmoothingMode = SmoothingMode.None;
        }
    }

    /// <summary>取得目前比例的縮小地圖（第一次用到時才產生，之後重複使用）。</summary>
    private Bitmap GetScaledMap()
    {
        if (_scaledCache.TryGetValue(_scale, out Bitmap? cached))
        {
            return cached;
        }

        // 最多留 4 種比例，避免記憶體用太多
        if (_scaledCache.Count >= 4)
        {
            ClearCache();
        }

        int w = Math.Max(1, (int)Math.Round(_map!.Width * _scale));
        int h = Math.Max(1, (int)Math.Round(_map.Height * _scale));
        Bitmap bmp = new(w, h, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = _scale == 1.0 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_map, new Rectangle(0, 0, w, h));
        }
        _scaledCache[_scale] = bmp;
        return bmp;
    }

    private void ClearCache()
    {
        foreach (Bitmap b in _scaledCache.Values)
        {
            b.Dispose();
        }
        _scaledCache.Clear();
    }

    #endregion

    #region 滑鼠操作

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Right && _map is not null)
        {
            LastRightClickWorld = WorldFromScreen(e.Location);
        }
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragStarted = false;
            _dragStart = e.Location;
            _dragLast = e.Location;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_map is not null)
        {
            MouseWorldMoved?.Invoke(this, WorldFromScreen(e.Location));
        }
        if (!_dragging || _map is null)
        {
            return;
        }

        if (!_dragStarted)
        {
            if (Math.Abs(e.X - _dragStart.X) < DragThreshold && Math.Abs(e.Y - _dragStart.Y) < DragThreshold)
            {
                return;
            }
            _dragStarted = true;
            UpdateCursor();
            // 開始拖曳就關掉跟隨，不然下一次讀到座標又會跳回船的位置
            FollowShip = false;
        }

        int dx = e.X - _dragLast.X;
        int dy = e.Y - _dragLast.Y;
        _dragLast = e.Location;
        SetFocus(_focusX - dx / PixelsPerWorldX(_scale), _focusY - dy / PixelsPerWorldY(_scale));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = false;
            _dragStarted = false;
            Capture = false;
            UpdateCursor();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        MouseWorldMoved?.Invoke(this, null);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && _map is not null)
        {
            // 要做什麼由主視窗決定（看按了哪些修飾鍵）
            MapDoubleClicked?.Invoke(this,
                new MapClickEventArgs(WorldFromScreen(e.Location), ModifierKeys, IsOnShip(e.Location), IsOnMarker(e.Location)));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        double step = e.Delta > 0 ? ScaleStep : -ScaleStep;
        ZoomAt(_scale + step, e.Location);
    }

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearCache();
            _map?.Dispose();
            _map = null;
            ShipIcon?.Dispose(); // 船的圖示是遊戲圖示的複本，交給 MapView 後由這裡負責釋放
            ShipIcon = null;
        }
        base.Dispose(disposing);
    }
}
