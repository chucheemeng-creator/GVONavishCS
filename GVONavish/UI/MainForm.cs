// MainForm.cs
// 主視窗：定時讀座標 → 過濾讀錯的值 → 更新地圖上的船。
//
// 操作方式（「操作說明…」選單裡也看得到）：
//   右鍵                    叫出選單
//   左鍵拖曳                移動地圖（會自動關掉「地圖跟隨船隻」）
//   雙擊                    在地圖上留下座標標籤；在標籤的十字上再雙擊一次就取消（雙擊船的圖示：回到船的位置並跟隨）
//   Shift + 雙擊            從船目前的位置規劃航線到這裡（和 GVONavi 相同）
//   Ctrl + 雙擊             從上一段規劃航線的終點，接著規劃到這裡（GVONavi 的 Ctrl + Shift + 雙擊也可以）
//   Backspace               刪掉最後一段規劃航線
//   Esc                     清除座標標籤；沒有標籤時清除規劃航線
//   空白鍵                  回到船的位置並跟隨
//   滑鼠滾輪、+ / -         放大縮小（沒跟隨船時，以滑鼠位置為中心）
//   F1                      視窗置頂 開／關
//   規劃航線時（Shift／Ctrl＋雙擊、右鍵「規劃航線到這裡」），附近（60 格內）有港口就自動對準港口；
//   雙擊留下座標標籤時不移動標籤，只在文字上註明附近的港口。
//
// 航線：每次讀到座標就加一個點；超過 5 秒沒讀到（測量結束、進港）就結束這一段，下次開新的一段。
//       航線存在程式資料夾的 routes.json，每 30 秒和關閉程式時自動存檔。
// 規劃航線：右鍵「規劃航線…」輸入座標，或在地圖上按右鍵選「規劃航線到這裡」。

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GVONavish.Core;

namespace GVONavish.UI;

public class MainForm : Form
{
    private const string AppName = "GVONavish";
    // 版本號只寫在 .csproj 的 <Version>，這裡從編譯好的程式讀出來，不用兩邊都改。
    // GetName().Version 是 0.9.4.0 這種四段格式，只取前三段。
    private static readonly string AppVersion =
        "C# 版 " + (typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "?");

    /// <summary>超過這麼久沒讀到座標，狀態列就顯示「看不到座標」。</summary>
    private const int CoordLostMs = 3000;

    /// <summary>超過這麼久沒讀到座標，就結束目前這段航線（和原版相同是 5 秒）。</summary>
    private const int RouteBreakMs = 5000;

    // 讀取間隔可以設到 5 秒，所以「多久沒讀到算中斷」要跟著間隔放寬，
    // 否則間隔 5 秒時每一筆都會被當成中斷（航線每點都斷開、航向一直重算）
    private long LostMs => Math.Max(CoordLostMs, _timer.Interval * 2L);
    private long BreakMs => Math.Max(RouteBreakMs, _timer.Interval * 5L / 2);

    private readonly AppSettings _settings;
    private readonly GameWindowCapture _capture = new();
    private readonly CoordFilter _filter = new();
    private readonly ShipRouteList _routes = new();
    private readonly ShipMotion _motion = new();
    private readonly SpeedMeter _speed = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    private readonly MapView _mapView = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _lblGame = new();
    private readonly ToolStripStatusLabel _lblCoord = new();
    private readonly ToolStripStatusLabel _lblScale = new();
    private readonly ToolStripStatusLabel _lblSaveError = new(); // 存檔失敗時才出現
    private readonly ToolStripStatusLabel _lblRejected = new();
    private readonly ToolStripStatusLabel _lblPlan = new();
    private readonly ToolStripStatusLabel _lblMouse = new();

    private readonly ContextMenuStrip _menu = new();
    private ToolStripMenuItem _miFollow = null!;
    private ToolStripMenuItem _miTopMost = null!;
    private ToolStripMenuItem _miHeadingLine = null!;
    private ToolStripMenuItem _miSpeed = null!;
    private ToolStripMenuItem _miSpeedCorner = null!;
    private ToolStripMenuItem _miRoutes = null!;
    private ToolStripMenuItem _miPlanHere = null!;
    private ToolStripMenuItem _miPlanClear = null!;
    private ToolStripMenuItem _miMarkerClear = null!;
    private ToolStripMenuItem _miPlanSave = null!;

    // 規劃航線
    private RoutePlanner? _planner;          // 海陸地圖準備好之後才有
    private string _plannerStatus = "準備中";  // 給使用者看的狀態
    private readonly SynchronizationContext _uiContext; // 背景工作做完後，用它回到畫面執行緒
    private readonly bool _routesCanSave = true;      // routes.json 讀不出來又無法改名保留時是 false，不存檔
    private bool _routesSaveFailed;          // 上一次存航線失敗（狀態列顯示警告）
    private bool _planning;                  // 正在規劃中（避免同時按兩次）
    private readonly System.Collections.Generic.List<PlannedRoute> _legs =
        new System.Collections.Generic.List<PlannedRoute>(); // 規劃航線的每一段
    private ToolStripMenuItem _miPlanUndo = null!;

    /// <summary>規劃航線時，離港口這麼近就自動對準港口；座標標籤也用這個距離註明「附近」的港口（世界座標單位）。</summary>
    private const double PortSnapDistance = 60;

    // 航線管理、存檔
    private RouteManagerForm? _routeManager;
    private bool _routesDirty;
    private readonly System.Windows.Forms.Timer _saveTimer = new();

    private long _lastReadTime;       // 最後一次成功讀到座標的時間
    private bool _iconLoaded;         // 已經取得遊戲圖示

    public MainForm(AppSettings settings)
    {
        // 畫面執行緒的 SynchronizationContext（WinForms 建立第一個控制項時就會裝好）。
        // 必須在建構子裡抓，欄位初始化時還沒有。
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = settings;
        if (_settings.LoadProblem is not null)
        {
            // 等主視窗出現後再說明，訊息框才不會跑到看不到的地方
            Shown += (s, e) => MessageBox.Show(this, _settings.LoadProblem, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        BuildUi();
        ApplySettings();
        _routes.ReplaceAll(RouteStore.Load(out string? routesProblem, out _routesCanSave));
        if (routesProblem is not null)
        {
            // 等主視窗出現後再說明，訊息框才不會跑到看不到的地方
            Shown += (s, e) => MessageBox.Show(this, routesProblem, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        // 航線有任何變動（記錄、刪除、連接、我的最愛、清除…）都要存檔並重畫地圖。
        // 統一在這裡處理，各處改航線時就不用各自記得呼叫 Invalidate()。
        // 航行中每 0.5 秒會觸發一次，但 Invalidate 只是標記「需要重畫」，多次會合併成一次，不影響效能。
        _routes.Changed += (s, e) =>
        {
            _routesDirty = true;
            _mapView.Invalidate();
        };
        StartLoadingSeaMask();
        LoadMapOnStartup();

        // 航線每 30 秒存檔一次（有變動才存），程式當掉也最多只少 30 秒
        _saveTimer.Interval = 30_000;
        _saveTimer.Tick += (s, e) =>
        {
            SaveRoutesIfDirty();
            SaveLastPosition();
        };
        _saveTimer.Start();

        _timer.Tick += (s, e) =>
        {
            // 第二層保護：讀座標過程中任何意外（例如顯示卡驅動程式重設），都只略過這一次，不讓程式當掉
            try
            {
                PollGame();
            }
            catch (Exception ex)
            {
                _lblGame.Text = "讀座標時發生錯誤，稍後重試：" + ex.Message;
            }
        };
        _timer.Interval = Math.Max(100, _settings.IntervalMs);
        _timer.Start();
    }

    #region 介面

    private void BuildUi()
    {
        Text = AppName;
        Font = new Font("Microsoft JhengHei UI", 9f);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        Icon = SystemIcons.Application;

        _mapView.Dock = DockStyle.Fill;
        _mapView.Routes = _routes;
        _mapView.ContextMenuStrip = _menu;
        _mapView.ViewChanged += (s, e) => UpdateTitleAndStatus();
        _mapView.FollowShipChanged += (s, e) => _miFollow.Checked = _mapView.FollowShip;
        _mapView.PlannedLegs = _legs;
        _mapView.MapDoubleClicked += (s, e) => OnMapDoubleClicked(e);
        _mapView.MouseWorldMoved += (s, p) => UpdateMouseLabel(p);
        Controls.Add(_mapView);

        _lblGame.Spring = true;
        _lblGame.TextAlign = ContentAlignment.MiddleLeft;
        _lblCoord.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _lblScale.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _lblRejected.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _lblRejected.ToolTipText = "讀到不合理的座標（例如畫面被擋住時讀錯）而沒有採用的次數";
        _lblSaveError.Text = "⚠ 存檔失敗";
        _lblSaveError.ForeColor = UiColors.Pick(Color.Firebrick, Color.FromArgb(255, 140, 110));
        _lblSaveError.Visible = false;
        _lblPlan.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _lblPlan.ForeColor = UiColors.Pick(Color.SeaGreen, Color.LightGreen);
        _lblPlan.Visible = false;
        _lblMouse.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _lblMouse.Visible = false;
        _statusStrip.Items.AddRange([_lblSaveError, _lblGame, _lblPlan, _lblMouse, _lblCoord, _lblScale, _lblRejected]);
        _statusStrip.ShowItemToolTips = true;
        Controls.Add(_statusStrip);

        BuildMenu();
    }

    private void BuildMenu()
    {
        _miFollow = new ToolStripMenuItem("地圖跟隨船隻", null, (s, e) => _mapView.FollowShip = !_mapView.FollowShip) { ShortcutKeyDisplayString = "空白鍵" };
        _miTopMost = new ToolStripMenuItem("視窗置頂", null, (s, e) => ToggleTopMost()) { ShortcutKeyDisplayString = "F1" };
        _miHeadingLine = new ToolStripMenuItem("顯示航向線", null, (s, e) => ToggleHeadingLine());
        _miSpeed = new ToolStripMenuItem("顯示船速", null, (s, e) => ToggleSpeed());
        _miSpeedCorner = new ToolStripMenuItem("船速位置");
        AddCornerItem("右上", ScreenCorner.TopRight);
        AddCornerItem("右下", ScreenCorner.BottomRight);
        AddCornerItem("左上", ScreenCorner.TopLeft);
        AddCornerItem("左下", ScreenCorner.BottomLeft);
        _miRoutes = new ToolStripMenuItem("顯示航跡", null, (s, e) => ToggleRoutes());
        ToolStripMenuItem miClearRoutes = new("清除航跡…", null, (s, e) => ClearRoutes());
        ToolStripMenuItem miRouteManager = new("航線管理…", null, (s, e) => ShowRouteManager());
        ToolStripMenuItem miPlan = new("規劃航線…", null, (s, e) => ShowPlanDialog());
        _miPlanHere = new ToolStripMenuItem("規劃航線到這裡", null, (s, e) => PlanToRightClick());
        _miPlanClear = new ToolStripMenuItem("清除規劃航線", null, (s, e) => ClearPlannedRoute()) { ShortcutKeyDisplayString = "Esc" };
        _miMarkerClear = new ToolStripMenuItem("清除座標標籤", null, (s, e) => ClearMarker()) { ShortcutKeyDisplayString = "Esc" };
        _miPlanUndo = new ToolStripMenuItem("刪掉最後一段規劃", null, (s, e) => UndoLastLeg()) { ShortcutKeyDisplayString = "Backspace" };
        ToolStripMenuItem miHelp = new("操作說明…", null, (s, e) => ShowHelp());
        _miPlanSave = new ToolStripMenuItem("把規劃航線存成我的最愛", null, (s, e) => SavePlannedRoute());
        ToolStripMenuItem miResetZoom = new("恢復 100% 比例", null, (s, e) => _mapView.ResetZoom());
        ToolStripMenuItem miZoomIn = new("放大", null, (s, e) => _mapView.ZoomIn()) { ShortcutKeyDisplayString = "+" };
        ToolStripMenuItem miZoomOut = new("縮小", null, (s, e) => _mapView.ZoomOut()) { ShortcutKeyDisplayString = "-" };
        ToolStripMenuItem miMap = new("選擇地圖圖檔…", null, (s, e) => ChooseMap());
        ToolStripMenuItem miCapture = new("讀座標設定…", null, (s, e) => ShowCaptureSettings());
        ToolStripMenuItem miColorMode = new("外觀");
        AddColorModeItem(miColorMode, "淺色", "Classic");
        AddColorModeItem(miColorMode, "深色", "Dark");
        AddColorModeItem(miColorMode, "跟隨 Windows 設定", "System");
        ToolStripMenuItem miAbout = new("版本資訊…", null, (s, e) => ShowAbout());
        ToolStripMenuItem miExit = new("結束", null, (s, e) => Close());

        _menu.Items.AddRange(
        [
            _miFollow, _miTopMost,
            new ToolStripSeparator(),
            _miHeadingLine, _miSpeed, _miSpeedCorner,
            new ToolStripSeparator(),
            _miPlanHere, miPlan, _miPlanSave, _miPlanUndo, _miPlanClear, _miMarkerClear,
            new ToolStripSeparator(),
            miRouteManager, _miRoutes, miClearRoutes,
            new ToolStripSeparator(),
            miZoomIn, miZoomOut, miResetZoom,
            new ToolStripSeparator(),
            miMap, miCapture, miColorMode,
            new ToolStripSeparator(),
            miHelp, miAbout, miExit,
        ]);

        // 打開選單時，依目前狀態決定哪些項目可以按
        _menu.Opening += (s, e) =>
        {
            bool hasPlan = _legs.Count > 0;
            _miPlanClear.Enabled = hasPlan;
            _miMarkerClear.Enabled = _mapView.Marker.HasValue;
            _miPlanSave.Enabled = hasPlan;
            _miPlanUndo.Enabled = hasPlan;
            _miPlanHere.Enabled = _mapView.HasMap && _mapView.LastRightClickWorld.HasValue;
            _miPlanHere.Text = _mapView.LastRightClickWorld.HasValue
                ? $"規劃航線到這裡（{PlaceName(_mapView.LastRightClickWorld.Value)}）"
                : "規劃航線到這裡";
        };
    }

    private void ApplySettings()
    {
        // 視窗位置：有存過、而且還在某個螢幕範圍內才使用
        Rectangle bounds = new(_settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight);
        if (bounds.Width > 100 && bounds.Height > 100 && IsOnAnyScreen(bounds))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }
        else
        {
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            Size = new Size(640, 480);
        }

        TopMost = _settings.TopMost;
        _miTopMost.Checked = TopMost;

        _capture.OffsetFromRightBottom = new Point(_settings.OffsetX, _settings.OffsetY);

        _mapView.FollowShip = _settings.FollowShip;
        _miFollow.Checked = _settings.FollowShip;
        _mapView.ViewScale = _settings.Scale;
        _mapView.ShowHeadingLine = _settings.ShowHeadingLine;
        _miHeadingLine.Checked = _settings.ShowHeadingLine;
        _mapView.ShowSpeed = _settings.ShowSpeed;
        _miSpeed.Checked = _settings.ShowSpeed;
        _mapView.ShowRoutes = _settings.ShowRoutes;
        _miRoutes.Checked = _settings.ShowRoutes;
        SetSpeedCorner(Enum.IsDefined(typeof(ScreenCorner), _settings.SpeedCorner) ? _settings.SpeedCorner : ScreenCorner.TopRight);
        _mapView.SetFocus(_settings.LastX, _settings.LastY);

        // 船先放在上次關閉時的位置：在港口裡（沒有測量）也能直接從這裡規劃航線。
        // 讀到新的座標後就換成真正的位置。
        WorldPoint last = new(_settings.LastX, _settings.LastY);
        if (World.IsValid(last))
        {
            _mapView.SetShipPosition(last);
        }

        UpdateTitleAndStatus();
    }

    /// <summary>
    /// 視窗的標題列有一段看得到、抓得到（至少 100 × 20 像素在某個螢幕裡）。
    /// 只看「有沒有重疊」不夠：只露出 1 個像素也算重疊，使用者卻沒辦法把視窗拖回來。
    /// </summary>
    private static bool IsOnAnyScreen(Rectangle r)
    {
        Rectangle titleBar = new(r.X, r.Y, r.Width, 30);
        foreach (Screen s in Screen.AllScreens)
        {
            Rectangle visible = Rectangle.Intersect(s.WorkingArea, titleBar);
            if (visible.Width >= 100 && visible.Height >= 20)
            {
                return true;
            }
        }
        return false;
    }

    #endregion

    #region 地圖

    private void LoadMapOnStartup()
    {
        string path = _settings.GetMapFullPath();
        if (path != "" && File.Exists(path) && TryLoadMap(path, showError: true))
        {
            return;
        }
        // 找不到地圖就請使用者選；不選也沒關係，畫面會顯示提示
        Shown += (s, e) => ChooseMap();
    }

    private bool TryLoadMap(string path, bool showError)
    {
        try
        {
            using Bitmap bmp = new(path);
            _mapView.SetMap(bmp);
            return true;
        }
        catch (Exception ex)
        {
            if (showError)
            {
                MessageBox.Show(this, "無法開啟地圖圖檔。\n\n" + path + "\n\n" + ex.Message,
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }
    }

    /// <summary>
    /// 在背景讀取海陸資料檔 seamask.bin（約 0.3 秒），完成後就能規劃航線。
    /// 規劃航線一律用這個檔案判斷海和陸地，所以畫面上換成哪張地圖都沒關係。
    /// </summary>
    private void StartLoadingSeaMask()
    {
        _planner = null;
        _plannerStatus = "準備中";
        string maskPath = SeaMap.DefaultMaskPath;

        Task.Run(() => SeaMap.LoadMask(maskPath)).ContinueWith(t =>
        {
            RunOnUi(() =>
            {
                SeaMap? sea = t.IsFaulted ? null : t.Result;
                if (sea is not null)
                {
                    _planner = new RoutePlanner(sea);
                    _plannerStatus = "";
                    return;
                }

                // 檔案不見了或壞掉：退回去分析附帶的 map.png（不管畫面上顯示哪張）
                StartBuildingSeaMap();
            });
        });
    }

    /// <summary>
    /// 背景工作做完後回到畫面執行緒執行。
    /// 用建構時記下的 SynchronizationContext 排進畫面執行緒的訊息佇列：視窗還沒建立好也能排，
    /// 等訊息迴圈開始跑就會執行；也不必在背景執行緒碰 IsHandleCreated、HandleCreated
    /// （以前那樣寫，視窗剛好在那一瞬間建立好時，action 可能被執行兩次）。
    /// </summary>
    private void RunOnUi(Action action)
    {
        _uiContext.Post(_ =>
        {
            if (!IsDisposed)
            {
                action();
            }
        }, null);
    }

    /// <summary>附帶的地圖（固定幾種顏色，分析得出海和陸地）。</summary>
    private static string BundledMapPath => Path.Combine(AppContext.BaseDirectory, "map.png");

    /// <summary>
    /// 沒有 seamask.bin 時的備案：在背景分析「附帶的 map.png」的海和陸地（約 2 秒），並存回 seamask.bin。
    /// 一律分析附帶的那張，和畫面上顯示哪張地圖無關：別張地圖顏色不同，就算「看起來」分析成功，
    /// 海陸也可能是錯的，不能拿來規劃航線。
    /// </summary>
    private void StartBuildingSeaMap()
    {
        const string missing = "找不到海陸資料檔 seamask.bin，請把程式附帶的 seamask.bin 放回程式資料夾";
        byte[] pixels;
        int w, h, stride;
        try
        {
            // 先在這裡把像素複製出來（Bitmap 不能跨執行緒用），分析交給背景
            using Bitmap bmp = new(BundledMapPath);
            w = bmp.Width;
            h = bmp.Height;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                stride = data.Stride;
                pixels = new byte[stride * h];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch
        {
            _plannerStatus = missing; // 附帶的 map.png 也不見了
            return;
        }

        _plannerStatus = "準備中";
        Task.Run(() =>
        {
            SeaMap sea = SeaMap.FromPixels(pixels, w, h, stride);
            if (sea.IsPlausible)
            {
                try
                {
                    sea.SaveMaskAtomic(SeaMap.DefaultMaskPath); // 下次啟動就不用再分析
                }
                catch
                {
                    // 存不了（例如程式放在唯讀資料夾）就算了，下次啟動再分析一次
                }
            }
            return sea;
        }).ContinueWith(t =>
        {
            RunOnUi(() =>
            {
                if (t.IsFaulted || !t.Result.IsPlausible)
                {
                    // 附帶的 map.png 被換成別張圖了
                    _plannerStatus = missing;
                    return;
                }
                _planner = new RoutePlanner(t.Result);
                _plannerStatus = "";
            });
        });
    }

    private void ChooseMap()
    {
        using OpenFileDialog dlg = new()
        {
            Title = "請選擇地圖圖檔",
            Filter = "圖檔|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.tif;*.tiff|所有檔案|*.*",
            InitialDirectory = AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        if (TryLoadMap(dlg.FileName, showError: true))
        {
            // 圖檔在程式資料夾裡的話存相對路徑，整個資料夾搬家也能用
            string baseDir = AppContext.BaseDirectory;
            _settings.MapFile = dlg.FileName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                ? dlg.FileName[baseDir.Length..]
                : dlg.FileName;
            _settings.Save();
        }
    }

    #endregion

    #region 讀座標

    private void PollGame()
    {
        if (!_capture.FindGameWindow())
        {
            _lblGame.Text = "找不到遊戲視窗";
            _iconLoaded = false;
            UpdateStale(Environment.TickCount64);
            return;
        }

        if (!_iconLoaded)
        {
            Bitmap? icon = _capture.GetGameIcon();
            if (icon is not null)
            {
                _mapView.ShipIcon?.Dispose();
                _mapView.ShipIcon = icon;
                _iconLoaded = true;
            }
        }

        long now = Environment.TickCount64;
        WorldPoint? read = _capture.CaptureAndRead();
        if (read.HasValue)
        {
            CoordFilterResult result = _filter.Feed(read.Value, now);
            if (result == CoordFilterResult.Accepted || result == CoordFilterResult.AcceptedJump)
            {
                OnShipMoved(_filter.Current!.Value, now, result == CoordFilterResult.AcceptedJump);
                _lblGame.Text = "讀取中";
            }
            else if (now - _lastReadTime > LostMs)
            {
                _lblGame.Text = "確認座標中…";
            }
        }
        else if (now - _lastReadTime > LostMs)
        {
            // 不在海上、沒發動「測量」技能、或座標被其他視窗擋住時，畫面上沒有座標
            _lblGame.Text = "看不到座標（不在海上，或未發動測量，或畫面被擋住）";
        }

        UpdateStale(now);
        UpdateTitleAndStatus();
    }

    /// <summary>一陣子沒讀到座標時，把船速和航向線變灰，表示那是舊的資料。</summary>
    private void UpdateStale(long now)
    {
        bool stale = _lastReadTime == 0 || now - _lastReadTime > LostMs;
        if (_mapView.Stale != stale)
        {
            _mapView.Stale = stale;
            _mapView.Invalidate();
        }
    }

    /// <summary>確定了一個新的船位置：更新航線、航向、船速和地圖。</summary>
    private void OnShipMoved(WorldPoint p, long now, bool jumped)
    {
        bool firstFix = _lastReadTime == 0;
        bool lostForAWhile = !firstFix && now - _lastReadTime > BreakMs;
        _motion.MaxGapSeconds = BreakMs / 1000.0;
        _speed.MaxGapMs = BreakMs;

        if (jumped || lostForAWhile)
        {
            // 一陣子沒讀到座標（測量結束、進港…），或船瞬間換了位置：
            // 結束目前這段航線，不然會畫出一條穿越陸地的直線
            _routes.CloseCurrent();
            _speed.Reset();
        }
        if (firstFix || jumped || lostForAWhile)
        {
            // 航向要從新位置重新推算（一陣子沒讀到座標時，舊的點已經沒有參考價值）
            _motion.Reset(p, now);
        }
        else
        {
            _motion.Update(p, now);
        }

        _speed.Update(p, now);
        _routes.AddPoint(p);
        _lastReadTime = now;

        _mapView.Heading = _motion.Heading;
        _mapView.HeadingDegrees = _motion.HeadingDegrees;
        _mapView.SpeedKnots = _speed.HasValue ? _speed.Knots : (double?)null;
        _mapView.SetShipPosition(p);
    }

    private void ToggleHeadingLine()
    {
        _mapView.ShowHeadingLine = !_mapView.ShowHeadingLine;
        _miHeadingLine.Checked = _mapView.ShowHeadingLine;
        _mapView.Invalidate();
    }

    private void ToggleSpeed()
    {
        _mapView.ShowSpeed = !_mapView.ShowSpeed;
        _miSpeed.Checked = _mapView.ShowSpeed;
        _mapView.Invalidate();
    }

    private void AddCornerItem(string text, ScreenCorner corner)
    {
        ToolStripMenuItem item = new(text, null, (s, e) => SetSpeedCorner(corner)) { Tag = corner };
        _miSpeedCorner.DropDownItems.Add(item);
    }

    private void SetSpeedCorner(ScreenCorner corner)
    {
        _mapView.SpeedCorner = corner;
        foreach (ToolStripMenuItem item in _miSpeedCorner.DropDownItems)
        {
            item.Checked = (ScreenCorner)item.Tag! == corner;
        }
        _mapView.Invalidate();
    }

    private void ToggleRoutes()
    {
        _mapView.ShowRoutes = !_mapView.ShowRoutes;
        _miRoutes.Checked = _mapView.ShowRoutes;
        _mapView.Invalidate();
    }

    private void ClearRoutes()
    {
        if (_routes.Routes.Count == 0)
        {
            return;
        }
        if (MessageBox.Show(this, "確定要清除地圖上所有的航跡嗎？\n（「我的最愛」會保留，要刪除請到航線管理）", AppName,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        _routes.ClearExceptFavorites(); // 會觸發 Changed：存檔、重畫地圖
    }

    private void ShowRouteManager()
    {
        if (_routeManager is null || _routeManager.IsDisposed)
        {
            _routeManager = new RouteManagerForm(_routes, _mapView) { Owner = this };
            RouteManagerForm form = _routeManager;
            // 位置、大小、欄寬要在 Load 時套用：視窗依螢幕縮放調整大小是在這之前做的，
            // 太早設定會被再縮放一次
            form.Load += (s, e) => RestoreRouteManagerLayout(form);
            // 在 FormClosing（視窗還完整可用）時記住，不在 FormClosed：之後如果改成關閉就 Dispose，也不會讀到已釋放的視窗
            form.FormClosing += (s, e) => RememberRouteManagerLayout(form);
        }
        _routeManager.TopMost = TopMost;
        _routeManager.Show();
        _routeManager.Activate();
    }

    /// <summary>還原上次的位置、大小和欄寬；沒存過或位置已經不在螢幕上，就開在主視窗旁邊。</summary>
    private void RestoreRouteManagerLayout(RouteManagerForm form)
    {
        form.SetColumnWidths(_settings.RouteManagerColumns);

        Rectangle saved = new(_settings.RouteManagerX, _settings.RouteManagerY,
            _settings.RouteManagerWidth, _settings.RouteManagerHeight);
        if (saved.Width >= form.MinimumSize.Width && saved.Height >= form.MinimumSize.Height && IsOnAnyScreen(saved))
        {
            form.Bounds = saved;
            return;
        }

        // 開在主視窗右邊；右邊放不下就放左邊，再放不下就整個移進螢幕裡
        Rectangle work = Screen.FromControl(this).WorkingArea;
        int w = form.Width, h = form.Height;
        int x = Right + 4;
        if (x + w > work.Right)
        {
            x = Left - w - 4;
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        int y = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - h));
        form.Location = new Point(x, y);
    }

    /// <summary>記住航線管理視窗的位置、大小和欄寬（關閉時，或主視窗關閉時它還開著）。</summary>
    private void RememberRouteManagerLayout(RouteManagerForm form)
    {
        Rectangle b = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        _settings.RouteManagerX = b.X;
        _settings.RouteManagerY = b.Y;
        _settings.RouteManagerWidth = b.Width;
        _settings.RouteManagerHeight = b.Height;
        _settings.RouteManagerColumns = form.GetColumnWidths();
        _settings.Save();
    }

    /// <summary>「外觀」子選單的一個選項。目前的設定打勾；換了之後要重新開程式才會套用。</summary>
    private void AddColorModeItem(ToolStripMenuItem parent, string text, string mode)
    {
        ToolStripMenuItem item = new(text) { Checked = _settings.ColorMode == mode };
        if (mode != "Classic" && !Program.SupportsDarkMode)
        {
            // WinForms 的深色模式只支援 Windows 11 以後；Windows 10 選了也不會變深色，所以不給選
            item.Enabled = false;
            item.Text += "（需要 Windows 11）";
        }
        item.Click += (s, e) =>
        {
            if (_settings.ColorMode == mode) return;
            _settings.ColorMode = mode;
            _settings.Save();
            foreach (ToolStripMenuItem other in parent.DropDownItems)
            {
                other.Checked = other == item;
            }
            MessageBox.Show(this, "外觀會在下次開啟程式時套用。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        parent.DropDownItems.Add(item);
    }

    private void SaveRoutesIfDirty()
    {
        if (_routesDirty && _routesCanSave)
        {
            _routesSaveFailed = !RouteStore.Save(_routes.Routes);
            if (!_routesSaveFailed)
            {
                _routesDirty = false;
            }
        }
        UpdateSaveWarning();
    }

    /// <summary>
    /// 設定檔或航線檔存不了（例如程式放在唯讀資料夾、磁碟滿了）時，狀態列最左邊顯示警告；之後存成功就消失。
    /// 原本的檔案不會被動到（先寫暫存檔），程式也照常運作，只是這次的變更還沒存下來。
    /// </summary>
    private void UpdateSaveWarning()
    {
        bool settingsFailed = _settings.LastSaveFailed;
        bool show = settingsFailed || _routesSaveFailed;
        if (_lblSaveError.Visible != show)
        {
            _lblSaveError.Visible = show;
        }
        if (show)
        {
            string what = settingsFailed && _routesSaveFailed ? "設定和航線"
                : settingsFailed ? "設定（settings.json）" : "航線（routes.json）";
            _lblSaveError.ToolTipText = $"{what}存不進程式資料夾。\n" +
                "可能是資料夾唯讀（例如放在 Program Files）、磁碟已滿，或檔案被其他程式占用。\n" +
                "原本的檔案沒有被動到；請把程式移到可以寫入的資料夾。";
        }
    }

    /// <summary>船的位置有變就存進設定檔（每 30 秒檢查一次），程式當掉也不會丟失太多。</summary>
    private void SaveLastPosition()
    {
        if (!_mapView.ShipPosition.HasValue) return;
        WorldPoint p = _mapView.ShipPosition.Value;
        if (p.X == _settings.LastX && p.Y == _settings.LastY) return;
        _settings.LastX = p.X;
        _settings.LastY = p.Y;
        _settings.Save();
    }

    #endregion

    #region 規劃航線

    private void ShowPlanDialog()
    {
        using PlanRouteForm dlg = new(_mapView.ShipPosition, _settings.PlanStart, _settings.PlanEnd, _settings.RecentPorts);
        dlg.TopMost = TopMost;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.PlanStart = dlg.StartLabel;
            PlanRoute(dlg.Start, dlg.End, dlg.StartLabel, dlg.EndLabel, append: false);
        }
    }

    /// <summary>雙擊地圖：依照按住的鍵決定要做什麼。</summary>
    private void OnMapDoubleClicked(MapClickEventArgs e)
    {
        bool shift = (e.Modifiers & Keys.Shift) != 0;
        bool ctrl = (e.Modifiers & Keys.Control) != 0;

        if (ctrl)
        {
            // Ctrl + 雙擊（或 GVONavi 的 Ctrl + Shift + 雙擊）：接著上一段規劃
            AppendLegTo(e.World);
        }
        else if (shift)
        {
            // Shift + 雙擊：從船目前的位置規劃
            PlanFromShipTo(e.World);
        }
        else if (e.OnShip)
        {
            // 雙擊船：回到船的位置並跟隨
            _mapView.FollowShip = true;
        }
        else if (e.OnMarker)
        {
            // 在座標標籤上再雙擊一次：取消標籤（只用滑鼠也能清掉）
            ClearMarker();
        }
        else
        {
            // 雙擊：留下座標標籤
            ShowMarker(e.World);
        }
    }

    /// <summary>點的位置附近有港口，就對準港口；回傳座標和名稱。</summary>
    private static (WorldPoint Point, string Name) SnapToPort(WorldPoint p)
    {
        Port? port = PortList.Nearest(p, PortSnapDistance);
        return port is not null ? (port.Position, port.Name) : (p, p.ToString());
    }

    private void ClearMarker()
    {
        _mapView.Marker = null;
        _mapView.Invalidate();
    }

    private void ShowMarker(WorldPoint p)
    {
        Port? port = PortList.Nearest(p, PortSnapDistance);
        _mapView.Marker = p;
        _mapView.MarkerText = port is not null ? $"{p}（{port.NameWithEnglish()} 附近）" : p.ToString();
        _mapView.Invalidate();
    }

    /// <summary>
    /// 從船目前的位置規劃到 p（取代原本的規劃航線）。
    /// 還不知道船的位置時（沒發動測量），改從座標標籤的位置出發；兩個都沒有就提示。
    /// </summary>
    private void PlanFromShipTo(WorldPoint p)
    {
        (WorldPoint target, string targetName) = SnapToPort(p);
        if (_mapView.ShipPosition.HasValue)
        {
            WorldPoint ship = _mapView.ShipPosition.Value;
            PlanRoute(ship, target, ShipStartName(ship), targetName, append: false);
        }
        else if (_mapView.Marker.HasValue)
        {
            (WorldPoint start, string startName) = SnapToPort(_mapView.Marker.Value);
            PlanRoute(start, target, startName, targetName, append: false);
        }
        else
        {
            MessageBox.Show(this,
                "還不知道船的位置（要在海上發動「測量」，或上次關閉程式時已經讀到過座標）。\n\n" +
                "也可以先雙擊一個地方當起點，再按住 Shift 雙擊終點；或用右鍵選「規劃航線…」輸入起點。",
                "規劃航線", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>從上一段規劃航線的終點接著規劃到 p。還沒有規劃航線時，等於 Shift + 雙擊。</summary>
    private void AppendLegTo(WorldPoint p)
    {
        if (_legs.Count == 0)
        {
            PlanFromShipTo(p);
            return;
        }
        PlannedRoute last = _legs[^1];
        (WorldPoint target, string targetName) = SnapToPort(p);
        PlanRoute(last.End, target, last.EndName, targetName, append: true);
    }

    /// <summary>記住最近用過的港口（不是港口的名稱，例如座標，就略過）。</summary>
    private void RememberPort(string name)
    {
        Port? port = PortList.Find(name);
        if (port is null) return;
        _settings.RecentPorts.Remove(port.Name);
        _settings.RecentPorts.Insert(0, port.Name);
        while (_settings.RecentPorts.Count > 10)
        {
            _settings.RecentPorts.RemoveAt(_settings.RecentPorts.Count - 1);
        }
        // 馬上存：船停在港口不動時，定時存檔（只在位置有變時存）不會存到這裡
        _settings.Save();
    }

    private void UndoLastLeg()
    {
        if (_legs.Count == 0) return;
        _legs.RemoveAt(_legs.Count - 1);
        _mapView.Invalidate();
        UpdateTitleAndStatus();
    }

    private void UpdateMouseLabel(WorldPoint? p)
    {
        if (!p.HasValue)
        {
            _lblMouse.Visible = false;
            return;
        }
        Port? port = PortList.Nearest(p.Value, PortSnapDistance);
        _lblMouse.Text = port is not null ? $"滑鼠 {p.Value}（{port.NameWithEnglish()} 附近）" : $"滑鼠 {p.Value}";
        _lblMouse.Visible = true;
    }

    /// <summary>
    /// 某個座標的顯示名稱：附近（約 60 格內）有港口就顯示「港口名稱附近」，沒有就顯示座標。
    /// </summary>
    private static string PlaceName(WorldPoint p)
    {
        Port? port = PortList.Nearest(p, PortSnapDistance);
        return port is not null ? $"{port.Name}附近 {p}" : p.ToString();
    }

    /// <summary>
    /// 從船的位置出發時的起點名稱：在港口裡或港口旁邊（約 60 格內）就用港口名稱，不然用座標。
    /// 起點座標仍然是船的實際位置，只是名稱用港口：這樣「最近用過的港口」記得到，
    /// 存成我的最愛時的自動名稱也是「里斯本 → 開普敦」，而不是「里斯本附近 15785, 3204 → 開普敦」。
    /// </summary>
    private static string ShipStartName(WorldPoint ship)
    {
        Port? port = PortList.Nearest(ship, PortSnapDistance);
        return port is not null ? port.Name : ship.ToString();
    }

    private void PlanToRightClick()
    {
        WorldPoint? end = _mapView.LastRightClickWorld;
        if (!end.HasValue)
        {
            return;
        }
        // 按的地方附近有港口，就直接以那個港口為終點（按不準也沒關係）
        (WorldPoint target, string targetName) = SnapToPort(end.Value);

        if (!_mapView.ShipPosition.HasValue)
        {
            // 還不知道船在哪：改用對話框讓使用者輸入起點
            _settings.PlanEnd = targetName;
            ShowPlanDialog();
            return;
        }
        WorldPoint ship = _mapView.ShipPosition.Value;
        PlanRoute(ship, target, ShipStartName(ship), targetName, append: false);
    }

    /// <param name="append">true：加在現有規劃航線的後面；false：取代原本的規劃航線。</param>
    private async void PlanRoute(WorldPoint start, WorldPoint end, string startName, string endName, bool append)
    {
        if (_planning)
        {
            return;
        }
        if (_planner is null)
        {
            string why = !_mapView.HasMap ? "請先選擇地圖圖檔。"
                : _plannerStatus == "準備中" ? "正在分析地圖的海和陸地，請過一兩秒再試一次。"
                : _plannerStatus + "。";
            MessageBox.Show(this, why, "規劃航線", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _settings.PlanEnd = endName;

        _planning = true;
        // 計算期間滑鼠顯示沙漏（地圖上、狀態列上都是）
        _mapView.Busy = true;
        UseWaitCursor = true;
        _lblPlan.Visible = true;
        _lblPlan.Text = "規劃中…";
        try
        {
            // 先抓到區域變數：規劃途中換地圖把 _planner 設成 null 也不影響這次計算
            RoutePlanner planner = _planner;
            PlannedRoute? route;
            try
            {
                route = await Task.Run(() => planner.Plan(start, end));
            }
            catch (Exception ex)
            {
                // async void 的例外沒人接，會跳出 WinForms 的錯誤視窗，所以在這裡接住
                if (IsDisposed) return;
                _lblPlan.Visible = _legs.Count > 0;
                _mapView.Busy = false;
                UseWaitCursor = false;
                MessageBox.Show(this, "規劃航線時發生錯誤：\n\n" + ex.Message,
                    "規劃航線", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (IsDisposed)
            {
                return; // 計算期間主視窗已經關掉了，不要再碰畫面
            }
            if (route is not null && route.Waypoints.Count < 2)
            {
                // 起點和終點是同一個地方（例如 Ctrl+雙擊同一個港口兩次）：不加一段看不見、長度 0 的航段
                _lblPlan.Visible = _legs.Count > 0;
                return;
            }
            if (route is null)
            {
                _lblPlan.Visible = _legs.Count > 0;
                _mapView.Busy = false;   // 先把沙漏換回來再跳出訊息
                UseWaitCursor = false;
                MessageBox.Show(this,
                    $"找不到從 {startName} 到 {endName} 的海上航線。\n\n起點或終點可能在內陸，或是被陸地完全包圍。",
                    "規劃航線", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                route.StartName = startName;
                route.EndName = endName;
                if (!append) _legs.Clear();
                _legs.Add(route);
                RememberPort(startName);
                RememberPort(endName);
                _mapView.Invalidate();
            }
        }
        finally
        {
            _planning = false;
            if (!IsDisposed)
            {
                _mapView.Busy = false;
                UseWaitCursor = false;
                UpdateTitleAndStatus();
            }
        }
    }

    /// <summary>清除規劃航線（座標標籤另外用 ClearMarker 清）。</summary>
    private void ClearPlannedRoute()
    {
        _legs.Clear();
        _mapView.Invalidate();
        UpdateTitleAndStatus();
    }

    /// <summary>把規劃航線存成一段「我的最愛」航線，之後在航線管理裡看得到，也會存檔。</summary>
    private void SavePlannedRoute()
    {
        if (_legs.Count == 0)
        {
            return;
        }
        // 自動取名，例如「里斯本 → 開普敦 → 卡利卡特」，之後可以在航線管理裡改
        System.Collections.Generic.List<string> names = [_legs[0].StartName];
        foreach (PlannedRoute leg in _legs) names.Add(leg.EndName);
        ShipRoute route = new() { Favorite = true, Name = string.Join(" → ", names) };
        foreach (PlannedRoute leg in _legs)
        {
            Vec2? prev = null;
            foreach (Vec2 p in leg.Waypoints)
            {
                // 存成航線後，兩點之間一律走「比較近的那一邊」（左右差不超過半張地圖）。
                // 規劃結果的 X 是攤開的，開闊海域可能有一段很長的直線（例如繞極區），
                // 直接存兩端點會變成往反方向繞半圈、穿過大陸。所以長的線段中間補點，每段左右差不超過 4000。
                if (prev is Vec2 a)
                {
                    int pieces = (int)Math.Ceiling(Math.Abs(p.X - a.X) / MaxSavedSegmentDx);
                    for (int k = 1; k < pieces; k++)
                    {
                        double f = (double)k / pieces;
                        route.AddPoint(ToWorldPoint(a.X + (p.X - a.X) * f, a.Y + (p.Y - a.Y) * f));
                    }
                }
                route.AddPoint(ToWorldPoint(p.X, p.Y));
                prev = p;
            }
        }
        _routes.AddFinished(route);
        _legs.Clear();
        _mapView.Invalidate();
        UpdateTitleAndStatus();
        ShowRouteManager();
        _routeManager!.SelectAndRename(route); // 已經自動取名，直接進入改名，按 Enter 就是接受這個名字
    }

    private const double MaxSavedSegmentDx = 4000;

    private static WorldPoint ToWorldPoint(double unwrappedX, double y)
    {
        return new WorldPoint((int)Math.Round(World.WrapX(unwrappedX)) % World.Width, (int)Math.Round(y));
    }

    /// <summary>規劃航線的狀態列文字：經過哪裡、總距離，以及照目前船速大概要多久。</summary>
    private string PlanStatusText()
    {
        System.Collections.Generic.List<string> names = [_legs[0].StartName];
        double length = 0;
        foreach (PlannedRoute leg in _legs)
        {
            names.Add(leg.EndName);
            length += leg.Length;
        }
        string path = names.Count <= 4
            ? string.Join(" → ", names)
            : $"{names[0]} → … → {names[^1]}";
        string legs = _legs.Count > 1 ? $"（{_legs.Count} 段）" : "";
        string text = $"規劃航線{legs}：{path}，距離 {length:0}";
        if (_speed.HasValue && _speed.UnitsPerSecond > 0.3)
        {
            TimeSpan eta = TimeSpan.FromSeconds(length / _speed.UnitsPerSecond);
            text += eta.TotalHours >= 1
                ? $"，照目前船速約 {(int)eta.TotalHours} 小時 {eta.Minutes} 分"
                : $"，照目前船速約 {eta.Minutes} 分 {eta.Seconds} 秒";
        }
        return text;
    }

    private void ShowCaptureSettings()
    {
        using CaptureSettingsForm dlg = new(_settings.OffsetX, _settings.OffsetY, _settings.IntervalMs);
        dlg.TopMost = TopMost;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.OffsetX = dlg.OffsetX;
            _settings.OffsetY = dlg.OffsetY;
            _settings.IntervalMs = dlg.IntervalMs;
            _capture.OffsetFromRightBottom = new Point(dlg.OffsetX, dlg.OffsetY);
            _timer.Interval = Math.Max(100, dlg.IntervalMs);
            _filter.Reset(); // 截圖位置改了，重新確認座標
            _settings.Save();
        }
    }

    #endregion

    #region 其他

    private void UpdateTitleAndStatus()
    {
        string scale = (_mapView.ViewScale * 100).ToString("0.0") + "%";
        WorldPoint? ship = _mapView.ShipPosition;

        // 標題列格式和原版相同：座標 - (比例) - 程式名稱
        Text = ship.HasValue
            ? $"{ship.Value.X},{ship.Value.Y} - ({scale}) - {AppName}"
            : $"({scale}) - {AppName}";

        // 還沒讀到座標時，船的位置是上次關閉時存下來的，標明一下
        string lastNote = ship.HasValue && _lastReadTime == 0 ? "（上次位置）" : "";
        _lblCoord.Text = ship.HasValue ? $"座標 {ship.Value.X}, {ship.Value.Y}{lastNote}" : "座標 ----";
        _lblScale.Text = "比例 " + scale;
        _lblRejected.Text = "擋掉 " + _filter.RejectedCount;
        UpdateSaveWarning();

        if (!_planning)
        {
            _lblPlan.Visible = _legs.Count > 0;
            if (_legs.Count > 0) _lblPlan.Text = PlanStatusText();
        }
    }

    private void ToggleTopMost()
    {
        TopMost = !TopMost;
        _miTopMost.Checked = TopMost;
        if (_routeManager is not null && !_routeManager.IsDisposed)
        {
            _routeManager.TopMost = TopMost; // 航線管理開著的話，一起切換
        }
    }

    private void ShowHelp()
    {
        MessageBox.Show(this,
            "滑鼠\n" +
            "　左鍵拖曳　　　　　移動地圖\n" +
            "　滾輪　　　　　　　放大縮小\n" +
            "　雙擊　　　　　　　在地圖上留下座標標籤（在標籤的十字上再雙擊一次就取消）\n" +
            "　雙擊船的圖示　　　回到船的位置並跟隨\n" +
            "　Shift＋雙擊　　　 從船目前的位置規劃航線到這裡\n" +
            "　Ctrl＋雙擊　　　　從上一段的終點接著規劃到這裡\n" +
            "　　　　　　　　　　（Ctrl＋Shift＋雙擊也可以）\n" +
            "　右鍵　　　　　　　選單\n\n" +
            "鍵盤\n" +
            "　空白鍵　　　　　　回到船的位置並跟隨\n" +
            "　Backspace　　　　 刪掉最後一段規劃航線\n" +
            "　Esc　　　　　　　 清除座標標籤；沒有標籤時清除規劃航線\n" +
            "　＋／－　　　　　　放大縮小\n" +
            "　F1　　　　　　　　視窗置頂\n\n" +
            "規劃航線時，附近有港口會自動對準港口；座標標籤只註明附近的港口，不會移動。\n" +
            "還不知道船的位置時，Shift＋雙擊會從座標標籤的位置出發。",
            "操作說明", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            $"{AppName} {AppVersion}\n\n" +
            "原作：@MandhelingFreak（2014）\n" +
            "英文翻譯：the_samedog\n" +
            "C# 移植版",
            "版本資訊", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F1:
                ToggleTopMost();
                return true;
            case Keys.Space:
                _mapView.FollowShip = true;
                return true;
            case Keys.Back:
                UndoLastLeg();
                return true;
            case Keys.Escape:
                // 一次清一樣：先清座標標籤，再按一次才清規劃航線
                if (_mapView.Marker is not null)
                {
                    ClearMarker();
                }
                else
                {
                    ClearPlannedRoute();
                }
                return true;
            case Keys.Add:
            case Keys.Oemplus:
            case Keys.Shift | Keys.Oemplus:
                _mapView.ZoomIn();
                return true;
            case Keys.Subtract:
            case Keys.OemMinus:
                _mapView.ZoomOut();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _saveTimer.Stop();
        _routes.CloseCurrent();
        _routesDirty = true;
        SaveRoutesIfDirty();

        if (_routeManager is not null && !_routeManager.IsDisposed && _routeManager.Visible)
        {
            RememberRouteManagerLayout(_routeManager); // 主視窗關閉時航線管理還開著
        }

        Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WindowX = b.X;
        _settings.WindowY = b.Y;
        _settings.WindowWidth = b.Width;
        _settings.WindowHeight = b.Height;
        _settings.TopMost = TopMost;
        _settings.FollowShip = _mapView.FollowShip;
        _settings.Scale = _mapView.ViewScale;
        _settings.ShowHeadingLine = _mapView.ShowHeadingLine;
        _settings.ShowSpeed = _mapView.ShowSpeed;
        _settings.ShowRoutes = _mapView.ShowRoutes;
        _settings.SpeedCorner = _mapView.SpeedCorner;
        if (_mapView.ShipPosition.HasValue)
        {
            _settings.LastX = _mapView.ShipPosition.Value.X;
            _settings.LastY = _mapView.ShipPosition.Value.Y;
        }
        _settings.Save();

        _capture.Dispose();
        base.OnFormClosing(e);
    }

    #endregion
}
