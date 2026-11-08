// ItemPickerForm.cs
// 手動指定一列對應到 Excel 的哪個發現物（「找不到」「不確定」「讀不出來」或是對錯的列）。
//
// 開啟時依「和畫面上讀到的文字有多像」排序，最像的在最上面；打字可以搜尋（包含這些字、或是很像的）。
// 分類判斷錯了也可以換分類。對目前選的人物已經打勾的，狀態欄會註明。
//
// 讀不出來的列（沒有文字可以比）：照 Excel 的順序列出，捲到上一列附近，但不預先選取（要自己看過名稱再選）。
// 讀到這一列的那一頁是依價值排列、而且上下兩列都知道時：遊戲的「依價值排列」就是依功績排序，
// 讀不出來的那一個功績一定介於上下兩列之間，功績在這個範圍、又還沒出現在清單上的用粗體標出來，並捲到最近的那個。
// 已經在清單上（這次讀到過）的標「已在清單上」，避免重複選到。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using GVODiscoverySync.Excel;
using GVODiscoverySync.Matching;

namespace GVODiscoverySync.UI;

public sealed class ItemPickerForm : Form
{
    // 清單最多列幾個（搜尋時太多也沒用）
    private const int MaxRows = 200;

    private readonly DiscoveryBook _book;
    private readonly string _screenText;
    private readonly int _character;
    // 讀不出來的列：上一列、下一列的名稱
    private readonly string _above;
    private readonly string _below;
    // 已經在清單上的發現物（這次讀到過、或手動指定過）
    private readonly IReadOnlySet<DiscoveryItem> _onList;
    // 讀到這一列時，那一頁是依價值排列：功績範圍才有意義（依名稱排列時功績是亂的）
    private readonly bool _pageByValue;

    // 讀不出來的列量到的字寬（遊戲像素，0 表示沒量到）：沒有搜尋時只列出字數對得上的
    private readonly double _textWidth;

    // 這一列目前對到（或推斷出來）的發現物：開啟時捲到它、預先選好，確認無誤直接按確定
    private readonly DiscoveryItem? _current;

    private readonly ComboBox _cboCategory = new();
    private readonly TextBox _txtSearch = new();
    private readonly ListView _list = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    /// <summary>選到的發現物（按「確定」後才有值）。</summary>
    public DiscoveryItem? SelectedItem { get; private set; }

    public ItemPickerForm(DiscoveryBook book, string category, string screenText, int character,
        IReadOnlySet<DiscoveryItem> onList, string above = "", string below = "", bool pageByValue = false,
        DiscoveryItem? current = null, double textWidth = 0)
    {
        _pageByValue = pageByValue;
        _textWidth = textWidth;
        _current = current;
        _book = book;
        _screenText = screenText;
        _character = character;
        _onList = onList;
        _above = above;
        _below = below;

        Text = "指定 Excel 發現物";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft JhengHei UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(420, 360);
        Size = new Size(720, 520);

        TableLayoutPanel top = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(8, 8, 8, 4),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        top.Controls.Add(MakeLabel("畫面上讀到："), 0, 0);
        top.Controls.Add(new Label
        {
            Text = (screenText == "" ? "（讀不出來）" : screenText)
                   + (textWidth > 0
                       ? $"　{Math.Round((textWidth + 2) / NameMatcher.FullWidthCharPixels)} 個字（只列出字數相同的；打字搜尋可以找全部）"
                       : ""),
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
            Font = new Font(Font, FontStyle.Bold),
        }, 1, 0);

        top.Controls.Add(MakeLabel("分類："), 0, 1);
        _cboCategory.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboCategory.Width = 160;
        _cboCategory.Items.AddRange([.. book.Categories.Keys]);
        _cboCategory.SelectedItem = book.Categories.ContainsKey(category) ? category : _cboCategory.Items[0];
        _cboCategory.SelectedIndexChanged += (_, _) => Fill();
        top.Controls.Add(_cboCategory, 1, 1);

        top.Controls.Add(MakeLabel("搜尋："), 0, 2);
        _txtSearch.Dock = DockStyle.Fill;
        _txtSearch.PlaceholderText = screenText == ""
            ? "打字縮小範圍；空白時照 Excel 的順序（依價值排列時，粗體是功績介於上下兩列之間、還沒讀到的）"
            : "打字縮小範圍（例如「鰭」）；空白時依和畫面文字的相似度排序";
        _txtSearch.TextChanged += (_, _) => Fill();
        _txtSearch.KeyDown += OnSearchKeyDown;
        top.Controls.Add(_txtSearch, 1, 2);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Columns.Add("Excel 上的名稱", 240);
        _list.Columns.Add("★", 40, HorizontalAlignment.Center);
        _list.Columns.Add("任務／地圖", 200);
        _list.Columns.Add("相似度", 60, HorizontalAlignment.Right);
        _list.Columns.Add("狀態", 100);
        _list.DoubleClick += (_, _) => Accept();
        _list.SelectedIndexChanged += (_, _) => _btnOk.Enabled = _list.SelectedItems.Count > 0;

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8, 4, 8, 8),
        };
        _btnCancel.Text = "取消";
        _btnCancel.AutoSize = true;
        _btnCancel.DialogResult = DialogResult.Cancel;
        _btnOk.Text = "確定";
        _btnOk.AutoSize = true;
        _btnOk.Click += (_, _) => Accept();
        buttons.Controls.AddRange([_btnCancel, _btnOk]);

        Controls.Add(_list);
        Controls.Add(buttons);
        Controls.Add(top);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        Fill();
        Shown += (_, _) => _txtSearch.Focus();
    }

    private static Label MakeLabel(string text)
        => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) };

    // 依目前的分類和搜尋文字重新列出候選
    private void Fill()
    {
        if (_cboCategory.SelectedItem is not string category || !_book.Categories.TryGetValue(category, out List<DiscoveryItem>? items))
        {
            return;
        }
        string search = NameMatcher.Normalize(_txtSearch.Text);
        string screen = NameMatcher.Normalize(_screenText);

        List<(DiscoveryItem Item, double Score)> rows = [];
        foreach (DiscoveryItem item in items)
        {
            if (search == "" && _textWidth > 0 && !NameMatcher.FitsWidth(item.Name, _textWidth))
            {
                continue; // 字數對不上（搜尋時不限制，萬一字寬量錯還找得到）
            }
            string name = NameMatcher.Normalize(item.Name);
            double score;
            if (search != "")
            {
                // 搜尋：包含這些字的排最前面，其次是很像的
                score = name.Contains(search, StringComparison.Ordinal) ? 1 + NameMatcher.Similarity(search, name)
                    : NameMatcher.Similarity(search, name);
                if (score < 0.4)
                {
                    continue;
                }
            }
            else
            {
                score = screen == "" ? 0 : NameMatcher.Similarity(screen, name);
            }
            rows.Add((item, score));
        }

        // 讀不出來、也沒有搜尋：照 Excel 的順序；功績介於上下兩列之間、還沒讀到的是候選
        bool excelOrder = search == "" && screen == "";
        int aboveIndex = excelOrder ? FindIndex(items, _above) : -1;
        int belowIndex = excelOrder ? FindIndex(items, _below) : -1;
        int meritLow = int.MinValue, meritHigh = int.MaxValue;
        if (excelOrder && (aboveIndex >= 0 || belowIndex >= 0))
        {
            if (aboveIndex >= 0 && belowIndex >= 0)
            {
                meritLow = Math.Min(items[aboveIndex].Merit, items[belowIndex].Merit);
                meritHigh = Math.Max(items[aboveIndex].Merit, items[belowIndex].Merit);
            }
            else if (aboveIndex >= 0)
            {
                meritHigh = items[aboveIndex].Merit; // 只知道上一列（清單的最後一列）：比它低的都可能
            }
            else
            {
                meritLow = items[belowIndex].Merit; // 只知道下一列（清單的第一列）：比它高的都可能
            }
        }
        // 上下兩列都知道才標粗體：只知道一邊時範圍是「比上一列低的全部」，會有半張表是粗體，沒有意義
        bool hasRange = excelOrder && _pageByValue && aboveIndex >= 0 && belowIndex >= 0;
        // 只列出字數對得上的時候，清單上的位置和 Excel 裡的位置不同：
        // Shown(i) 是清單上排在 Excel 第 i 個之前的有幾列（也就是第 i 個之後第一列在清單上的位置）
        Dictionary<DiscoveryItem, int> position = items.Select((item, i) => (item, i)).ToDictionary(p => p.item, p => p.i);
        int Shown(int itemIndex) => rows.Count(r => position[r.Item] < itemIndex);
        IEnumerable<(DiscoveryItem Item, double Score)> ordered = excelOrder
            ? rows
            : rows.OrderByDescending(r => r.Score).ThenBy(r => r.Item.Name, StringComparer.Ordinal).Take(MaxRows);

        _list.BeginUpdate();
        _list.Items.Clear();
        int index = 0;
        int nearest = -1; // 離上一列（沒有的話下一列）最近的候選
        int anchor = aboveIndex >= 0 ? Shown(aboveIndex) : belowIndex >= 0 ? Shown(belowIndex) : -1;
        int currentIndex = -1;
        foreach ((DiscoveryItem item, double score) in ordered)
        {
            bool onList = _onList.Contains(item);
            // 功績介於上下兩列之間、還沒讀到的：最可能就是讀不出來的那一個
            bool candidate = hasRange && !onList && item.Merit >= meritLow && item.Merit <= meritHigh;
            if (candidate && (nearest < 0 || Math.Abs(index - anchor) < Math.Abs(nearest - anchor)))
            {
                nearest = index;
            }
            if (_current is not null && item == _current)
            {
                currentIndex = index;
            }
            index++;
            // 搜尋時「包含」加了 1 分，顯示時拿掉
            double shown = score > 1 ? score - 1 : score;
            ListViewItem view = new([
                item.Name,
                item.Stars,
                item.Quest,
                search == "" && screen == "" ? "" : shown.ToString("0%", CultureInfo.InvariantCulture),
                onList ? "已在清單上" : item == _current ? "目前的結果" : item.Marked[_character] ? "已經打勾" : "",
            ]) { Tag = item };
            if (onList || item.Marked[_character])
            {
                view.ForeColor = UiColors.PickForList(Color.Gray, Color.DarkGray);
            }
            if (candidate)
            {
                view.Font = BoldFont;
            }
            _list.Items.Add(view);
        }
        _list.EndUpdate();

        if (currentIndex >= 0 && search == "")
        {
            // 這一列目前的結果（例如讀不出來、程式推斷的「爵」）：捲到它並選好，確認無誤直接按確定
            ListViewItem current = _list.Items[currentIndex];
            current.Selected = true;
            current.Focused = true;
            _list.TopItem = _list.Items[Math.Max(0, currentIndex - 4)];
            current.EnsureVisible();
        }
        else if (_list.Items.Count > 0)
        {
            // Excel 順序（讀不出來的列）時捲到最近的候選附近；沒有候選時捲到上一列的下一個（或下一列的前一個）
            int target = 0;
            if (nearest >= 0)
            {
                target = nearest;
            }
            else if (excelOrder && aboveIndex >= 0)
            {
                target = Math.Min(Shown(aboveIndex + 1), _list.Items.Count - 1);
            }
            else if (excelOrder && belowIndex >= 0 && Shown(belowIndex) > 0)
            {
                target = Shown(belowIndex) - 1;
            }
            if (excelOrder)
            {
                // 讀不出來的列只是提示位置，不預先選取：要使用者自己看過名稱點一下，才能按確定（不會按 Enter 就誤選）
                _list.Items[target].Focused = true;
                if (target > 0)
                {
                    _list.TopItem = _list.Items[Math.Max(0, target - 4)];
                }
            }
            else
            {
                // 有畫面文字可以比：預選最像的那一個
                _list.Items[target].Selected = true;
                _list.Items[target].Focused = true;
            }
            _list.Items[target].EnsureVisible();
        }
        _btnOk.Enabled = _list.SelectedItems.Count > 0;
    }

    // 候選列用的粗體字（共用一個，不要每次 Fill 都建立新的 GDI 字型；
    // 顯示縮放改變時清單的字型會跟著變，大小不同時重建）
    private Font? _boldFont;

    private Font BoldFont
    {
        get
        {
            if (_boldFont is null || _boldFont.Size != _list.Font.Size || _boldFont.FontFamily.Name != _list.Font.FontFamily.Name)
            {
                _boldFont?.Dispose();
                _boldFont = new Font(_list.Font, FontStyle.Bold);
            }
            return _boldFont;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _boldFont?.Dispose();
            _boldFont = null;
        }
        base.Dispose(disposing);
    }

    // name 在 items 裡的位置：先找完全一樣的，再找最像的（上一列可能是辨識出來的文字，不是 Excel 的寫法）
    private static int FindIndex(List<DiscoveryItem> items, string name)
    {
        if (name == "")
        {
            return -1;
        }
        int exact = items.FindIndex(i => i.Name == name);
        if (exact >= 0)
        {
            return exact;
        }
        string key = NameMatcher.Normalize(name);
        int best = -1;
        double bestScore = 0.6;
        for (int i = 0; i < items.Count; i++)
        {
            double s = NameMatcher.Similarity(key, NameMatcher.Normalize(items[i].Name));
            if (s > bestScore)
            {
                bestScore = s;
                best = i;
            }
        }
        return best;
    }

    // 搜尋框裡按上下鍵可以直接移動清單的選取
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is not (Keys.Down or Keys.Up) || _list.Items.Count == 0)
        {
            return;
        }
        int index;
        if (_list.SelectedIndices.Count > 0)
        {
            index = Math.Clamp(_list.SelectedIndices[0] + (e.KeyCode == Keys.Down ? 1 : -1), 0, _list.Items.Count - 1);
        }
        else
        {
            // 還沒選（讀不出來的列只捲到附近）：從焦點那一列往上或往下移一列
            int focused = _list.FocusedItem?.Index ?? -1;
            index = focused < 0 ? 0 : Math.Clamp(focused + (e.KeyCode == Keys.Down ? 1 : -1), 0, _list.Items.Count - 1);
        }
        _list.Items[index].Selected = true;
        _list.Items[index].EnsureVisible();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void Accept()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not DiscoveryItem item)
        {
            return;
        }
        SelectedItem = item;
        DialogResult = DialogResult.OK;
        Close();
    }
}
