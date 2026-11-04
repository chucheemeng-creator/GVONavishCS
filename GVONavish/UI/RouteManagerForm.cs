// RouteManagerForm.cs
// 航線管理視窗：列出所有航線，可以加入我的最愛、和上一段連接、刪除、在地圖上顯示，以及勾選要不要顯示。
// 對應原版的 GVOShipRouteManageView。
//
// 選取一段航線時，地圖上那段會用橘紅色、加粗畫出來。
// 每一列前面的勾選框決定這段航線要不要顯示在地圖上（選取中的航線就算沒勾也會畫出來）。
// 勾選框旁邊是名稱：按 F2、「重新命名」，或在選取的那一列名稱上再點一下，就可以直接打字。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GVONavish.Core;

namespace GVONavish.UI;

public class RouteManagerForm : Form
{
    private readonly ShipRouteList _routes;
    private readonly MapView _mapView;
    private readonly RouteListView _list = new();
    private readonly Button _btnFavorite = new();
    private readonly Button _btnRename = new();
    private readonly Button _btnJoin = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnShow = new();
    private readonly Button _btnDeselect = new();

    // 清單裡由上到下對應的航線（新 → 舊）
    private readonly List<ShipRoute> _rows = [];

    private bool _updatingChecks; // 程式在設定勾選框時，不要當成使用者勾的
    private bool _editingName; // 正在改名：這期間不重新整理清單，免得打字到一半被打斷

    public RouteManagerForm(ShipRouteList routes, MapView mapView)
    {
        _routes = routes;
        _mapView = mapView;
        BuildUi();
        RefreshList();
        _routes.Changed += OnRoutesChanged;
    }

    private void BuildUi()
    {
        Text = "航線管理";
        Font = new Font("Microsoft JhengHei UI", 9f);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(680, 360);
        MinimumSize = new Size(420, 200);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(4),
            WrapContents = true,
        };
        SetupButton(_btnFavorite, "★ 我的最愛", (s, e) => ToggleFavorite());
        SetupButton(_btnRename, "重新命名", (s, e) => BeginRename());
        SetupButton(_btnJoin, "與上一段連接", (s, e) => JoinWithPrevious());
        SetupButton(_btnShow, "在地圖上顯示", (s, e) => ShowOnMap());
        SetupButton(_btnDeselect, "取消選取", (s, e) => _list.SelectedIndices.Clear());
        SetupButton(_btnDelete, "刪除", (s, e) => DeleteSelected());
        buttons.Controls.AddRange([_btnFavorite, _btnRename, _btnJoin, _btnShow, _btnDeselect, _btnDelete]);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.GridLines = true;
        _list.CheckBoxes = true;
        _list.LabelEdit = true;
        _list.Columns.Add("顯示／名稱", 160);
        _list.Columns.Add("★", 28, HorizontalAlignment.Center);
        _list.Columns.Add("建立時間", 110);
        _list.Columns.Add("出發點", 90);
        _list.Columns.Add("終點", 90);
        _list.Columns.Add("距離", 60, HorizontalAlignment.Right);
        _list.Columns.Add("航行時間", 70, HorizontalAlignment.Right);
        _list.SelectedIndexChanged += (s, e) => OnSelectionChanged();
        _list.DoubleClick += (s, e) => ShowOnMap();
        _list.ItemCheck += (s, e) =>
        {
            if (_updatingChecks) return;
            if (_list.SuppressCheck)
            {
                e.NewValue = e.CurrentValue; // 雙擊一列時 ListView 自己切換的：擋掉
                return;
            }
            _routes.SetHidden(_rows[e.Index], e.NewValue != CheckState.Checked); // 會觸發 Changed：存檔、重畫地圖
        };
        _list.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteSelected();
            if (e.KeyCode == Keys.F2) BeginRename();
        };
        _list.BeforeLabelEdit += (s, e) => _editingName = true;
        _list.AfterLabelEdit += (s, e) =>
        {
            _editingName = false;
            e.CancelEdit = true; // 清單上的文字由 RefreshList 依航線資料更新（會去掉前後空白）
            if (e.Label is not null) // null：按了 Esc，沒改
            {
                _routes.SetName(_rows[e.Item], e.Label); // 會觸發 Changed：存檔、更新清單
            }
            BeginInvoke(new Action(RefreshList)); // 改名期間略過的更新，補做一次
        };

        ContextMenuStrip menu = new();
        menu.Items.Add("加入／取消我的最愛", null, (s, e) => ToggleFavorite());
        menu.Items.Add(new ToolStripMenuItem("重新命名", null, (s, e) => BeginRename()) { ShortcutKeyDisplayString = "F2" });
        menu.Items.Add("與上一段航線連接", null, (s, e) => JoinWithPrevious());
        menu.Items.Add("在地圖上顯示", null, (s, e) => ShowOnMap());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("全部顯示", null, (s, e) => _routes.SetAllHidden(false));
        menu.Items.Add("全部隱藏", null, (s, e) => _routes.SetAllHidden(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("刪除航線", null, (s, e) => DeleteSelected());
        _list.ContextMenuStrip = menu;

        Controls.Add(_list);
        Controls.Add(buttons);
        UpdateButtons();
    }

    private static void SetupButton(Button b, string text, EventHandler onClick)
    {
        b.Text = text;
        b.AutoSize = true;
        b.Click += onClick;
    }

    private ShipRoute? Selected => _list.SelectedIndices.Count > 0 ? _rows[_list.SelectedIndices[0]] : null;

    private void OnRoutesChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || _editingName) return;
        RefreshList();
    }

    /// <summary>重新整理清單。航線數量沒變時只更新文字（記錄中的航線每 0.5 秒就會變），畫面不會閃。</summary>
    private void RefreshList()
    {
        ShipRoute? selected = Selected;
        List<ShipRoute> routes = [.. _routes.Routes];
        routes.Reverse(); // 新的在上面

        bool sameRows = routes.Count == _rows.Count;
        for (int i = 0; sameRows && i < routes.Count; i++)
        {
            if (routes[i] != _rows[i]) sameRows = false;
        }

        _list.BeginUpdate();
        _updatingChecks = true;
        if (!sameRows)
        {
            _list.Items.Clear();
            _rows.Clear();
            foreach (ShipRoute r in routes)
            {
                _rows.Add(r);
                _list.Items.Add(new ListViewItem(""));
            }
        }
        for (int i = 0; i < _rows.Count; i++)
        {
            UpdateRow(_list.Items[i], _rows[i]);
        }
        if (!sameRows && selected is not null)
        {
            int idx = _rows.IndexOf(selected);
            if (idx >= 0) _list.Items[idx].Selected = true;
        }
        _updatingChecks = false;
        _list.EndUpdate();
        UpdateButtons();
    }

    private void UpdateRow(ListViewItem item, ShipRoute r)
    {
        if (item.Checked == r.Hidden) item.Checked = !r.Hidden;
        SetText(item, 0, r.Name);
        SetText(item, 1, r.Favorite ? "★" : "");
        SetText(item, 2, r.StartTime.ToString("MM/dd HH:mm"));
        SetText(item, 3, r.FirstPoint?.ToString() ?? "-");
        SetText(item, 4, r.LastPoint?.ToString() ?? "-");
        SetText(item, 5, Math.Round(r.Length).ToString("0"));
        TimeSpan d = r.EndTime - r.StartTime;
        SetText(item, 6, d.TotalHours >= 1 ? $"{(int)d.TotalHours}時{d.Minutes:00}分" : $"{d.Minutes}分{d.Seconds:00}秒");
        // 記錄中的航線用藍字（深色模式用淺藍；高對比模式用系統顏色）
        item.ForeColor = r.Closed ? SystemColors.WindowText
            : UiColors.PickForList(Color.Blue, Color.FromArgb(110, 180, 255));
    }

    private static void SetText(ListViewItem item, int col, string text)
    {
        while (item.SubItems.Count <= col) item.SubItems.Add("");
        if (item.SubItems[col].Text != text) item.SubItems[col].Text = text;
    }

    private void OnSelectionChanged()
    {
        ShipRoute? sel = Selected;
        foreach (ShipRoute r in _routes.Routes)
        {
            r.Highlight = r == sel;
        }
        _mapView.Invalidate();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        ShipRoute? sel = Selected;
        _btnFavorite.Enabled = sel is not null;
        _btnRename.Enabled = sel is not null;
        _btnShow.Enabled = sel is not null;
        _btnDeselect.Enabled = sel is not null;
        _btnDelete.Enabled = sel is not null;
        _btnJoin.Enabled = sel is not null && _routes.Routes.IndexOf(sel) > 0;
        _btnFavorite.Text = sel is not null && sel.Favorite ? "☆ 取消我的最愛" : "★ 我的最愛";
    }

    private void ToggleFavorite()
    {
        ShipRoute? sel = Selected;
        if (sel is null) return;
        // 會觸發 Changed：清單更新（OnRoutesChanged → RefreshList）、主視窗存檔並重畫地圖
        _routes.SetFavorite(sel, !sel.Favorite);
        if (sel.Favorite)
        {
            BeginRename(); // 剛加入我的最愛：直接取名字
        }
    }

    /// <summary>選取這段航線並進入改名（例如剛把規劃航線存成我的最愛）。</summary>
    public void SelectAndRename(ShipRoute route)
    {
        int index = _rows.IndexOf(route);
        if (index < 0) return;
        _list.SelectedIndices.Clear();
        _list.Items[index].Selected = true;
        _list.Items[index].EnsureVisible();
        BeginRename();
    }

    /// <summary>每一欄目前的寬度（記到設定檔用）。</summary>
    public List<int> GetColumnWidths()
    {
        List<int> widths = [];
        foreach (ColumnHeader c in _list.Columns)
        {
            widths.Add(c.Width);
        }
        return widths;
    }

    /// <summary>還原欄寬。欄數對不上（例如新版多了一欄）就不套用，用預設寬度。</summary>
    public void SetColumnWidths(IReadOnlyList<int> widths)
    {
        if (widths.Count != _list.Columns.Count) return;
        for (int i = 0; i < widths.Count; i++)
        {
            if (widths[i] > 0) _list.Columns[i].Width = widths[i];
        }
    }

    private void BeginRename()
    {
        if (_list.SelectedItems.Count == 0) return;
        ListViewItem item = _list.SelectedItems[0];
        // 等視窗出現、清單更新完再開始編輯（剛開視窗或剛改完資料時，馬上編輯可能沒反應或被打斷）
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || item.ListView is null) return;
            _list.Focus();
            item.BeginEdit();
        }));
    }

    private void JoinWithPrevious()
    {
        ShipRoute? sel = Selected;
        if (sel is null) return;
        _routes.JoinWithPrevious(sel);
    }

    private void DeleteSelected()
    {
        ShipRoute? sel = Selected;
        if (sel is null) return;
        string msg = sel.Favorite ? "這是「我的最愛」航線，確定要刪除嗎？" : "確定要刪除這段航線嗎？";
        if (MessageBox.Show(this, msg, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        _routes.Remove(sel);
    }

    /// <summary>把地圖移到這段航線（看得到整段的中間），並關掉跟隨船隻。</summary>
    private void ShowOnMap()
    {
        ShipRoute? sel = Selected;
        if (sel is null || sel.IsEmpty) return;

        // 攤開 X 算出範圍中心（避免跨過地圖邊界時算錯）
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        double? ux = null;
        int lastRaw = 0;
        foreach (List<WorldPoint> line in sel.Lines)
        {
            foreach (WorldPoint p in line)
            {
                ux = ux.HasValue ? ux.Value + World.WrappedDeltaX(lastRaw, p.X) : p.X;
                lastRaw = p.X;
                minX = Math.Min(minX, ux.Value);
                maxX = Math.Max(maxX, ux.Value);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }
        }
        _mapView.FollowShip = false;
        _mapView.SetFocus((minX + maxX) / 2, (minY + maxY) / 2);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _routes.Changed -= OnRoutesChanged;
        foreach (ShipRoute r in _routes.Routes)
        {
            r.Highlight = false;
        }
        _mapView.Invalidate();
        base.OnFormClosed(e);
    }
}

/// <summary>
/// 航線管理用的清單：處理勾選框和雙擊的衝突。
/// Windows 的 ListView 雙擊一列時，會自動切換那一列的勾選框；但雙擊在這裡是「在地圖上顯示」。
///   - 雙擊勾選框：當成兩次單擊（勾選框本身單擊就會切換），不觸發「在地圖上顯示」。
///   - 雙擊勾選框以外的地方：照常觸發 DoubleClick（在地圖上顯示），但不切換勾選框。
/// 這裡直接處理 Windows 的雙擊訊息，不靠滑鼠事件的先後順序（那個順序在 ListView 上不可靠）。
/// </summary>
internal sealed class RouteListView : ListView
{
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    /// <summary>true 的期間，ItemCheck 要把切換擋掉（雙擊一列造成的）。</summary>
    public bool SuppressCheck { get; private set; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_LBUTTONDBLCLK)
        {
            long lp = m.LParam.ToInt64();
            int x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF);
            ListViewItem? item = GetItemAt(x, y);
            // 勾選框在第一欄文字的左邊
            bool onCheckBox = item is not null && x < item.GetBounds(ItemBoundsPortion.Label).Left;
            if (onCheckBox)
            {
                m.Msg = WM_LBUTTONDOWN; // 當成單擊：切換勾選框，不算雙擊
                base.WndProc(ref m);
                return;
            }
            SuppressCheck = true; // 切換勾選框的通知是在 base.WndProc 裡面同步發生的
            try
            {
                base.WndProc(ref m);
            }
            finally
            {
                SuppressCheck = false;
            }
            return;
        }
        base.WndProc(ref m);
    }
}
