// PlanRouteForm.cs
// 「規劃航線」對話框：起點、終點可以輸入港口名稱，或直接輸入座標。
//
// 找港口：
//   - 打中文、英文、區域名稱的任何一部分都可以，例如「坦堡」「istan」「sayl」「加勒比」。
//   - 下面的「候選港口」清單會即時列出符合的港口；還沒打字時列出最近用過的港口。
//   - 用 ↑↓ 選擇，按 Enter 或點一下就填入；填好起點會自動跳到終點。
//   - 終點的候選港口用雙擊：填入並直接開始規劃。
//   - 用滑鼠點進輸入框時會全選，直接打字就取代原本的內容。
// 座標：直接貼上遊戲裡看到的格式，例如「15765,3222」或「15765 3222」。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using GVONavish.Core;

namespace GVONavish.UI;

public class PlanRouteForm : Form
{
    private readonly TextBox _txtStart = new();
    private readonly TextBox _txtEnd = new();
    private readonly Label _lblStartInfo = new();
    private readonly Label _lblEndInfo = new();
    private readonly Label _lblListTitle = new();
    private readonly ListBox _list = new();
    private readonly WorldPoint? _shipPosition;
    private readonly IReadOnlyList<string> _recentPorts;
    private TextBox _active;
    private bool _selectAllOnMouseUp;  // 剛用滑鼠點進輸入框：放開滑鼠時全選
    private TextBox? _lastClickFilled; // 上一次點清單時填入的是哪一格（判斷雙擊要不要直接規劃）

    public WorldPoint Start { get; private set; }
    public WorldPoint End { get; private set; }

    /// <summary>起點、終點的名稱（港口名稱，或座標文字），顯示在狀態列用。</summary>
    public string StartLabel { get; private set; } = "";
    public string EndLabel { get; private set; } = "";

    /// <param name="shipPosition">船目前的位置（沒有上次的起點時，起點預設用這個）。</param>
    /// <param name="lastStart">上次的起點文字；空字串表示上次用的是船的位置。</param>
    /// <param name="lastEnd">上次的終點文字（港口名稱或座標）。</param>
    /// <param name="recentPorts">最近用過的港口名稱（新的在前）。</param>
    public PlanRouteForm(WorldPoint? shipPosition, string lastStart, string lastEnd, IReadOnlyList<string> recentPorts)
    {
        _shipPosition = shipPosition;
        _recentPorts = recentPorts;
        _active = _txtEnd;
        BuildUi();
        _txtStart.Text = lastStart.Length > 0 ? lastStart : shipPosition?.ToString() ?? "";
        _txtEnd.Text = lastEnd;
        UpdateInfo(_txtStart, _lblStartInfo);
        UpdateInfo(_txtEnd, _lblEndInfo);
        RefreshList();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 起點通常是目前位置，直接從終點開始打
        _txtEnd.Focus();
        _txtEnd.SelectAll();
    }

    private void BuildUi()
    {
        Text = "規劃航線";
        Font = new Font("Microsoft JhengHei UI", 10f);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        TableLayoutPanel layout = new()
        {
            ColumnCount = 3,
            Padding = new Padding(10),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        Controls.Add(layout);

        SetupBox(_txtStart, _lblStartInfo);
        SetupBox(_txtEnd, _lblEndInfo);

        Button btnHere = new() { Text = "目前位置", AutoSize = true, Enabled = _shipPosition.HasValue };
        btnHere.Click += (s, e) => _txtStart.Text = _shipPosition!.Value.ToString();
        Button btnSwap = new() { Text = "起終點對調", AutoSize = true };
        btnSwap.Click += (s, e) => (_txtStart.Text, _txtEnd.Text) = (_txtEnd.Text, _txtStart.Text);

        layout.Controls.Add(new Label { Text = "起點：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_txtStart, 1, 0);
        layout.Controls.Add(btnHere, 2, 0);
        layout.Controls.Add(_lblStartInfo, 1, 1);
        layout.SetColumnSpan(_lblStartInfo, 2);
        layout.Controls.Add(new Label { Text = "終點：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_txtEnd, 1, 2);
        layout.Controls.Add(btnSwap, 2, 2);
        layout.Controls.Add(_lblEndInfo, 1, 3);
        layout.SetColumnSpan(_lblEndInfo, 2);

        _lblListTitle.AutoSize = true;
        _lblListTitle.Margin = new Padding(3, 6, 3, 2);
        layout.Controls.Add(_lblListTitle, 0, 4);
        layout.SetColumnSpan(_lblListTitle, 3);

        _list.Width = 380;
        _list.Height = 200;
        _list.IntegralHeight = false;
        _list.TabStop = false;
        _list.Click += (s, e) => _lastClickFilled = PickSelected();
        // 雙擊終點的候選港口 → 填入並直接規劃。
        // 雙擊時第一下已經由 Click 填好了；如果第一下填的是起點，清單已經換成終點的候選，第二下不算數。
        _list.DoubleClick += (s, e) =>
        {
            if (_lastClickFilled == _txtEnd && _list.SelectedItem is Port p && PortList.Find(_txtEnd.Text) == p)
            {
                TryAccept();
            }
        };
        layout.Controls.Add(_list, 0, 5);
        layout.SetColumnSpan(_list, 3);

        Label hint = new()
        {
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            ForeColor = SystemColors.GrayText,
            Text = "打港口的中文、英文或區域名稱的任何一部分（例如 坦堡、istan、加勒比），\n" +
                   "↑↓ 選擇，Enter 或點一下填入，終點雙擊直接規劃。也可以直接輸入座標，例如 15765,3222。",
            Margin = new Padding(3, 6, 3, 3),
        };
        layout.Controls.Add(hint, 0, 6);
        layout.SetColumnSpan(hint, 3);

        FlowLayoutPanel buttons = new() { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        Button btnOk = new() { Text = "規劃", AutoSize = true };
        Button btnCancel = new() { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        btnOk.Click += (s, e) => TryAccept();
        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnCancel);
        layout.Controls.Add(buttons, 0, 7);
        layout.SetColumnSpan(buttons, 3);

        // 不設 AcceptButton：輸入框裡的 Enter 自己處理（選港口／跳下一格／規劃）
        CancelButton = btnCancel;
    }

    private void SetupBox(TextBox box, Label info)
    {
        box.Width = 220;
        // 說明文字（港口名稱、座標）不自動調整大小：以前跟著港口名稱長短變寬變窄，整個視窗也跟著變。
        // 現在固定佔「輸入框＋右邊按鈕」的寬度，太長就在尾巴顯示「…」
        info.AutoSize = false;
        info.AutoEllipsis = true;
        info.Dock = DockStyle.Fill;
        info.Height = 24;
        info.ForeColor = SystemColors.GrayText;
        info.Margin = new Padding(3, 0, 3, 6);

        box.Enter += (s, e) =>
        {
            _active = box;
            RefreshList();
            // 用滑鼠點進來時，Windows 會在放開滑鼠時把游標放到點的位置、取消全選，
            // 所以這裡先記下來，等 MouseUp 再全選（用 Tab 進來時 Windows 本來就會全選）
            // 滑鼠要真的在這個輸入框上（點清單時程式把焦點移過來，不算）
            _selectAllOnMouseUp = (Control.MouseButtons & MouseButtons.Left) != 0
                && box.ClientRectangle.Contains(box.PointToClient(Cursor.Position));
        };
        box.Leave += (s, e) => _selectAllOnMouseUp = false;
        box.MouseUp += (s, e) =>
        {
            // 只有「點進來的那一下」才全選；已經在輸入框裡再點，是要移動游標，不全選。
            // 如果那一下是拖曳選了一段文字，也尊重使用者的選取。
            if (_selectAllOnMouseUp && box.SelectionLength == 0)
            {
                box.SelectAll();
            }
            _selectAllOnMouseUp = false;
        };
        box.TextChanged += (s, e) =>
        {
            UpdateInfo(box, info);
            if (box == _active) RefreshList();
        };
        box.KeyDown += (s, e) => OnBoxKeyDown(box, e);
    }

    // ↑↓ 在候選清單裡移動；Enter 選港口、跳到下一格，或直接規劃
    private void OnBoxKeyDown(TextBox box, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
        {
            if (_list.Items.Count > 0)
            {
                int i = _list.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1);
                _list.SelectedIndex = Math.Clamp(i, 0, _list.Items.Count - 1);
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;

            // 有選清單裡的港口，而且和輸入框內容不同 → 填入那個港口
            if (_list.SelectedItem is Port p && PortList.Find(box.Text) != p)
            {
                PickSelected();
                return;
            }
            // 已經填好了 → 起點就跳到終點，終點就開始規劃
            if (box == _txtStart && TryResolve(_txtStart.Text, out _, out _))
            {
                _txtEnd.Focus();
                _txtEnd.SelectAll();
            }
            else
            {
                TryAccept();
            }
        }
    }

    /// <summary>把清單選中的港口填進目前的輸入框；填的是起點就跳到終點。回傳填入的輸入框，沒填回傳 null。</summary>
    private TextBox? PickSelected()
    {
        if (_list.SelectedItem is not Port p) return null;
        TextBox box = _active;
        box.Text = p.Name;
        if (box == _txtStart)
        {
            _txtEnd.Focus();
            _txtEnd.SelectAll();
        }
        else
        {
            box.Focus();
            box.SelectionStart = box.Text.Length;
        }
        return box;
    }

    /// <summary>依目前輸入框的內容更新候選清單。</summary>
    private void RefreshList()
    {
        string q = _active.Text.Trim();
        List<Port> items;

        if (q == "" || PortList.Find(q) is not null)
        {
            // 還沒打字（或已經是完整的港口名稱）：列出最近用過的，後面接全部港口
            List<Port> recent = [.. _recentPorts.Select(PortList.Find).Where(p => p is not null).Cast<Port>()];
            var rest = PortList.All.Where(p => !recent.Contains(p)).OrderBy(p => p.Region).ThenBy(p => p.Name);
            items = [.. recent.Concat(rest)];
            _lblListTitle.Text = recent.Count > 0
                ? $"候選港口：最近用過的 {recent.Count} 個在最上面"
                : $"候選港口（全部 {PortList.All.Count} 個，依區域排列）";
        }
        else if (TryParse(q, out _))
        {
            items = [];
            _lblListTitle.Text = "候選港口：（輸入的是座標）";
        }
        else
        {
            items = PortList.Search(q);
            _lblListTitle.Text = items.Count > 0 ? $"候選港口：找到 {items.Count} 個" : "候選港口：找不到符合的港口";
        }

        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Items.AddRange([.. items]);
        if (items.Count > 0)
        {
            // 輸入框已經是某個港口時選中它，否則選第一個（直接按 Enter 就是第一個）
            Port? current = PortList.Find(q);
            int idx = current is not null ? items.IndexOf(current) : 0;
            _list.SelectedIndex = q == "" ? -1 : Math.Max(0, idx);
        }
        _list.EndUpdate();
    }

    // 輸入框下面的小字：顯示解讀的結果，打錯時馬上看得出來
    private static void UpdateInfo(TextBox box, Label info)
    {
        if (TryResolve(box.Text, out WorldPoint p, out _))
        {
            Port? port = PortList.Find(box.Text);
            info.Text = port is not null ? $"{port.NameWithEnglish("　")}：{p}" : $"座標 {p}";
            info.ForeColor = SystemColors.GrayText;
        }
        else if (box.Text.Trim() == "")
        {
            info.Text = "";
        }
        else
        {
            // 打字途中：提示按 Enter 會選到哪一個；完全找不到才用紅字
            Port? first = PortList.Search(box.Text).FirstOrDefault();
            info.Text = first is not null ? $"按 Enter 選：{first.NameWithEnglish("　")}" : "找不到這個港口，座標格式也不對";
            // 深色模式用亮一點的藍和紅；高對比模式用系統顏色
            info.ForeColor = first is not null
                ? UiColors.Pick(Color.SteelBlue, Color.FromArgb(110, 180, 255))
                : UiColors.Pick(Color.Firebrick, Color.FromArgb(255, 110, 110));
        }
    }

    private void TryAccept()
    {
        if (!TryResolve(_txtStart.Text, out WorldPoint s, out string sLabel))
        {
            ShowError("起點還沒填好。請從清單選一個港口，或輸入兩個數字的座標，例如 15765,3222。", _txtStart);
            return;
        }
        if (!TryResolve(_txtEnd.Text, out WorldPoint e, out string eLabel))
        {
            ShowError("終點還沒填好。請從清單選一個港口，或輸入兩個數字的座標，例如 15765,3222。", _txtEnd);
            return;
        }
        Start = s;
        End = e;
        StartLabel = sLabel;
        EndLabel = eLabel;
        DialogResult = DialogResult.OK;
    }

    private void ShowError(string msg, TextBox box)
    {
        MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        box.Focus();
        box.SelectAll();
    }

    /// <summary>港口名稱或座標 → 座標。label 是港口名稱，或座標文字。</summary>
    public static bool TryResolve(string text, out WorldPoint p, out string label)
    {
        Port? port = PortList.Find(text);
        if (port is not null)
        {
            p = port.Position;
            label = port.Name;
            return true;
        }
        if (TryParse(text, out p))
        {
            label = p.ToString();
            return true;
        }
        label = "";
        return false;
    }

    /// <summary>讀出兩個數字，中間用逗號、空白或其他符號隔開都可以。</summary>
    public static bool TryParse(string text, out WorldPoint p)
    {
        p = default;
        MatchCollection m = Regex.Matches(text ?? "", @"\d+");
        if (m.Count != 2) return false;
        if (!int.TryParse(m[0].Value, out int x) || !int.TryParse(m[1].Value, out int y)) return false;
        p = new WorldPoint(x, y);
        return World.IsValid(p);
    }
}
