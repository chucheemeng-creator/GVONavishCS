// MainForm.cs
// 發現物同步的主視窗。
//
// 用法：
//   1. 選 Excel 檔案、選人物
//   2. 遊戲裡打開「發現物」→ 選分類 →「一覽顯示」
//   3. 按「開始讀取」，在遊戲裡從頭捲到尾（按 ▼ 或滾輪都可以）；每次畫面停下來就讀一次，讀過的不會重複
//   4. 換下一個分類，一樣捲一遍
//   5. 檢查清單（「找不到」「不確定」的手動處理），按「寫入 Excel」
//
// 程式只看畫面，不會對遊戲按任何按鍵或滑鼠。這個視窗不要擋住遊戲的清單。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using GVO.Common.Capture;
using GVODiscoverySync.Excel;
using GVODiscoverySync.Matching;
using GVODiscoverySync.Ocr;

namespace GVODiscoverySync.UI;

public sealed class MainForm : Form
{
    // 讀到的一筆結果
    private sealed class Found
    {
        public required string Category { get; set; }
        public required string ScreenText { get; init; }
        public required MatchResult Match { get; init; }
        public bool HasCard { get; set; }
        public ListViewItem? View { get; set; }

        /// <summary>同名的一組、或手動指定時，實際對應到哪一個（其他時候是 null）。</summary>
        public DiscoveryItem? Assigned { get; set; }

        /// <summary>使用者手動指定的。</summary>
        public bool Manual { get; set; }

        /// <summary>放在「對不到」那一邊時的索引鍵（從那邊移走時要用）。</summary>
        public string? UnmatchedKey { get; set; }

        /// <summary>同名的一組還沒全部看到，不確定是組裡的哪一個。</summary>
        public bool VariantUnsure { get; set; }

        /// <summary>這一列有字但讀不出來（Note 說明它在哪兩個發現物之間）。</summary>
        public bool Unreadable { get; init; }

        public string Note { get; init; } = "";

        /// <summary>讀不出來的列：上一列、下一列的名稱（手動指定時用來捲到附近）。</summary>
        public string Above { get; init; } = "";
        public string Below { get; init; } = "";

        public DiscoveryItem? Item => Assigned ?? Match.Item;
    }

    private readonly AppSettings _settings;
    private readonly GameWindow _window = new();
    private readonly DiscoveryListReader? _reader;
    private readonly System.Windows.Forms.Timer _timer = new();

    private DiscoveryBook? _book;
    private NameMatcher? _matcher;

    // 對到 Excel 的（每個發現物只會出現一次）、對不到或不確定的（依分類＋畫面上的文字）
    private readonly Dictionary<DiscoveryItem, Found> _matched = [];
    private readonly Dictionary<string, Found> _unmatched = [];
    // 同名的一組（以組裡第一個為代表）→ 在同一頁最多看到幾個
    private readonly Dictionary<DiscoveryItem, int> _variantSeen = [];
    // 手動指定或移除過的列（分類＋畫面上的文字）：繼續捲動時又讀到同樣的文字，不要再加回來
    private readonly HashSet<string> _resolvedKeys = [];
    // 同名的一組（以組裡第一個為代表）→ 使用者移除、或改指定到組外的筆數（補的時候要算進去）
    private readonly Dictionary<DiscoveryItem, int> _variantSuppressed = [];
    // 同名的一組 → 看過的每一個（用上下鄰居的名稱分辨：鄰居不同就是不同的一個）
    private readonly Dictionary<DiscoveryItem, List<HashSet<string>>> _variantOccurrences = [];

    private bool _busy;
    // 上一次截圖、上一次辨識時，畫面上字的位置（判斷捲動停了沒、讀過了沒）
    private byte[]? _lastMask;
    private byte[]? _processedMask;

    private readonly TextBox _txtExcel = new();
    private readonly Button _btnBrowse = new();
    private readonly ComboBox _cboCharacter = new();
    private readonly Button _btnStart = new();
    private readonly Button _btnWrite = new();
    private readonly Button _btnClear = new();
    private readonly Button _btnSaveScreen = new();
    private readonly Button _btnTestFile = new();
    private readonly Label _lblStatus = new();
    private readonly Label _lblSummary = new();
    private readonly ListView _list = new();

    public MainForm(AppSettings settings)
    {
        _settings = settings;
        // 還沒有設定檔時先寫一份預設的（放在程式資料夾），方便直接編輯錯字對照表等設定
        if (!File.Exists(AppSettings.FilePath))
        {
            settings.Save();
        }
        _reader = DiscoveryListReader.TryCreate(settings.Layout);
        if (_reader is not null)
        {
            _reader.OcrScale = settings.OcrScale;
        }
        BuildUi();
        RestoreWindow();
        TopMost = settings.TopMost;
        UpdateTitle();
        KeyPreview = true; // F1 在任何控制項上都有效

        _timer.Interval = _settings.IntervalMs;
        _timer.Tick += OnTimerTick;

        Shown += (_, _) => OnShownFirstTime();
    }

    private void BuildUi()
    {
        UpdateTitle();
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft JhengHei UI", 9F);
        MinimumSize = new Size(640, 400);
        Size = new Size(820, 600);

        TableLayoutPanel top = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(8, 8, 8, 0),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // 第 1 列：Excel 檔案
        top.Controls.Add(new Label { Text = "Excel：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) }, 0, 0);
        _txtExcel.ReadOnly = true;
        _txtExcel.Dock = DockStyle.Fill;
        top.Controls.Add(_txtExcel, 1, 0);
        _btnBrowse.Text = "選擇…";
        _btnBrowse.AutoSize = true;
        _btnBrowse.Click += (_, _) => BrowseExcel();
        top.Controls.Add(_btnBrowse, 2, 0);

        // 第 2 列：人物、按鈕
        top.Controls.Add(new Label { Text = "人物：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) }, 0, 1);
        FlowLayoutPanel buttons = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        _cboCharacter.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboCharacter.Width = 140;
        _cboCharacter.SelectedIndexChanged += (_, _) => OnCharacterChanged();
        buttons.Controls.Add(_cboCharacter);
        SetupButton(_btnStart, "開始讀取", (_, _) => ToggleReading());
        SetupButton(_btnWrite, "寫入 Excel", (_, _) => WriteExcel());
        SetupButton(_btnClear, "清除結果", (_, _) => ClearResults());
        SetupButton(_btnSaveScreen, "存下遊戲畫面", async (_, _) => await SaveScreenAsync());
        SetupButton(_btnTestFile, "用截圖檔測試…", async (_, _) => await TestFromFileAsync());
        buttons.Controls.AddRange([_btnStart, _btnWrite, _btnClear, _btnSaveScreen, _btnTestFile]);
        top.Controls.Add(buttons, 1, 1);
        top.SetColumnSpan(buttons, 2);

        // 第 3 列：狀態
        _lblStatus.AutoSize = true;
        _lblStatus.Margin = new Padding(0, 6, 0, 4);
        top.Controls.Add(_lblStatus, 0, 2);
        top.SetColumnSpan(_lblStatus, 3);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.CheckBoxes = true;
        _list.GridLines = false;
        _list.Columns.Add("分類", 80);
        _list.Columns.Add("遊戲畫面上的名稱", 200);
        _list.Columns.Add("Excel 上的名稱", 200);
        _list.Columns.Add("相似度", 60, HorizontalAlignment.Right);
        _list.Columns.Add("卡片", 45, HorizontalAlignment.Center);
        _list.Columns.Add("狀態", 120);
        ContextMenuStrip menu = new();
        ToolStripMenuItem assignItem = new("指定 Excel 發現物…", null, (_, _) => AssignSelected()) { ShortcutKeyDisplayString = "Enter" };
        ToolStripMenuItem removeItem = new("移除這一筆", null, (_, _) => RemoveSelected()) { ShortcutKeyDisplayString = "Delete" };
        menu.Items.AddRange([assignItem, removeItem]);
        menu.Opening += (_, e) =>
        {
            bool has = SelectedFound is not null;
            assignItem.Enabled = has && _book is not null;
            removeItem.Enabled = has;
            e.Cancel = !has;
        };
        _list.ContextMenuStrip = menu;
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                AssignSelected();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                RemoveSelected();
                e.Handled = true;
            }
        };
        _list.ShowItemToolTips = true;

        _lblSummary.Dock = DockStyle.Bottom;
        _lblSummary.AutoSize = false;
        _lblSummary.Height = 26;
        _lblSummary.TextAlign = ContentAlignment.MiddleLeft;
        _lblSummary.Padding = new Padding(8, 0, 8, 0);

        Controls.Add(_list);
        Controls.Add(_lblSummary);
        Controls.Add(top);

        FillCharacterNames();
        UpdateButtons();
        UpdateSummary();
    }

    private static void SetupButton(Button button, string text, EventHandler click)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Click += click;
    }

    private void OnShownFirstTime()
    {
        if (_settings.LoadProblem is not null)
        {
            MessageBox.Show(this, _settings.LoadProblem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (_reader is null)
        {
            string installed = DiscoveryListReader.InstalledLanguages();
            MessageBox.Show(this,
                "這台電腦沒有繁體中文的文字辨識，沒辦法讀遊戲畫面。\n\n" +
                "請到 Windows「設定 → 時間與語言 → 語言與地區」新增「中文（繁體，台灣）」，" +
                "並確認語言選項裡有「光學字元辨識」，裝好後重新開啟本程式。\n\n" +
                $"目前已安裝的辨識語言：{(installed == "" ? "（沒有）" : installed)}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus("沒有繁體中文的文字辨識，只能讀 Excel，不能讀遊戲畫面。");
        }
        else
        {
            SetStatus($"文字辨識：{_reader.LanguageName}。在遊戲裡打開「發現物 → 一覽顯示」，再按「開始讀取」。");
        }

        if (_settings.ExcelPath != "" && File.Exists(_settings.ExcelPath))
        {
            LoadExcel(_settings.ExcelPath);
        }
    }

    // ── Excel ──

    private void BrowseExcel()
    {
        using OpenFileDialog dlg = new()
        {
            Title = "選擇發現物統計 Excel",
            Filter = "Excel 活頁簿 (*.xlsx)|*.xlsx",
        };
        if (_settings.ExcelPath != "")
        {
            dlg.InitialDirectory = Path.GetDirectoryName(_settings.ExcelPath) ?? "";
        }
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            LoadExcel(dlg.FileName);
        }
    }

    private void LoadExcel(string path)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            DiscoveryBook book = DiscoveryBook.Load(path);
            _book = book;
            _matcher = new NameMatcher(book, _settings.OcrConfusions);
            _settings.ExcelPath = path;
            _txtExcel.Text = path;
            FillCharacterNames();
            // 換了 Excel，之前讀到的結果對不上了，清掉重新讀
            bool hadResults = _list.Items.Count > 0;
            ClearResults();
            SetStatus($"已讀取 Excel：{book.Categories.Count} 個分類、{book.ItemCount} 個發現物。" +
                      (hadResults ? "之前讀到的結果已清除，請重新捲一次清單。" : ""));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Excel 讀不出來：\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
            UpdateButtons();
            UpdateSummary();
        }
    }

    private void FillCharacterNames()
    {
        string[] names = _book?.CharacterNames ?? ["人物1", "人物2", "人物3", "人物4", "人物5"];
        int selected = _cboCharacter.SelectedIndex >= 0 ? _cboCharacter.SelectedIndex : _settings.Character;
        _cboCharacter.BeginUpdate();
        _cboCharacter.Items.Clear();
        for (int i = 0; i < names.Length; i++)
        {
            _cboCharacter.Items.Add($"{i + 1}. {names[i]}");
        }
        _cboCharacter.EndUpdate();
        _cboCharacter.SelectedIndex = Math.Clamp(selected, 0, names.Length - 1);
    }

    private int Character => Math.Max(0, _cboCharacter.SelectedIndex);

    private void OnCharacterChanged()
    {
        _settings.Character = Character;
        // 換人物後「已打勾」的狀態不同，全部重新判斷（勾選回到預設）
        foreach (Found f in _matched.Values.Concat(_unmatched.Values))
        {
            UpdateRow(f, resetCheck: true);
        }
        UpdateSummary();
    }

    private void WriteExcel()
    {
        if (_book is null)
        {
            return;
        }
        int character = Character;
        List<DiscoveryItem> items = [.. _matched.Values.Concat(_unmatched.Values)
            .Where(f => f.View is { Checked: true } && f.Item is not null && !f.Item.Marked[character])
            .Select(f => f.Item!)
            .Distinct()];
        if (items.Count == 0)
        {
            MessageBox.Show(this, "沒有要寫入的發現物（勾選「新增」的項目才會寫入）。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string name = _book.CharacterNames[character];
        if (MessageBox.Show(this,
                $"要幫「{name}」在 {items.Count} 個發現物打勾嗎？\n\n寫入前會先在同一個資料夾備份原檔。\n寫入時 Excel 必須是關閉的。",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        bool wasRunning = _timer.Enabled;
        _timer.Stop();
        try
        {
            Cursor = Cursors.WaitCursor;
            string backup = _book.WriteMarks(character, items);
            foreach (Found f in _matched.Values.Concat(_unmatched.Values))
            {
                UpdateRow(f, resetCheck: true);
            }
            UpdateSummary();
            MessageBox.Show(this, $"已寫入 {items.Count} 筆。\n\n原檔備份為：\n{Path.GetFileName(backup)}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "寫入失敗：\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
            if (wasRunning)
            {
                _timer.Start();
            }
        }
    }

    // ── 讀遊戲畫面 ──

    private void ToggleReading()
    {
        if (_timer.Enabled)
        {
            _timer.Stop();
            SetStatus("已停止讀取。");
        }
        else
        {
            _lastMask = null;
            _processedMask = null;
            _timer.Start();
            SetStatus("讀取中…在遊戲裡慢慢捲動清單，每次停下來就會讀一次。");
        }
        UpdateButtons();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (_busy || _reader is null || _matcher is null)
        {
            return;
        }
        _busy = true;
        try
        {
            if (!_window.TryGetClientArea(out _, out double scale))
            {
                SetStatus("找不到遊戲視窗（或視窗縮到最小了）。");
                return;
            }
            using Bitmap? client = _window.CaptureClient();
            if (client is null)
            {
                SetStatus("截不到遊戲畫面（螢幕鎖定、或遊戲視窗被擋住？）");
                return;
            }
            await ProcessFrameAsync(client, scale, live: true);
        }
        catch (Exception ex)
        {
            SetStatus("讀取時發生錯誤：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 讀一張遊戲畫面。live＝true 時是連續讀取：畫面還在變（捲動中）就先不讀，
    /// 和上一次讀過的一樣也不重讀。
    /// </summary>
    private async Task ProcessFrameAsync(Bitmap client, double scale, bool live)
    {
        if (_reader is null || _matcher is null)
        {
            return;
        }
        byte[] mask = _reader.TextMask(client, scale);
        if (live)
        {
            bool stable = DiscoveryListReader.SameText(mask, _lastMask);
            _lastMask = mask;
            if (!stable)
            {
                return; // 畫面還在變（捲動中），等下一次確認停下來了
            }
            if (DiscoveryListReader.SameText(mask, _processedMask))
            {
                return; // 讀過了
            }
        }
        _processedMask = mask;

        NameMatcher matcher = _matcher;
        ListSnapshot snapshot = await _reader.ReadAsync(client, scale, t => matcher.MatchCategory(t) is not null);
        // 分類：先看標題；標題讀不到（常常如此）就從清單上的名稱推測
        string? category = _matcher.MatchCategory(snapshot.TitleText);
        string how = "";
        if (category is null)
        {
            category = _matcher.GuessCategory([.. snapshot.Rows.Where(r => r.Text != "").Select(r => r.Text)]);
            how = "（分類由清單內容判斷）";
        }
        if (category is null)
        {
            SetStatus(snapshot.Rows.Count == 0
                ? "目前畫面不是「發現物一覽」（清單上讀不到字）。"
                : "看不出這一頁是哪個分類（清單上的名稱在 Excel 裡對不太到），這一頁先跳過。");
            return;
        }

        // 每一列從不同倍率讀到的結果裡挑最像的，不確定的再用同一頁的其他列推斷
        List<(ListRow Row, string Text, MatchResult Match)> picks = [];
        foreach (ListRow row in snapshot.Rows)
        {
            (string text, MatchResult match) = PickBestReading(category, row);
            picks.Add((row, text, match));
        }
        ResolveWithinPage(category, picks);

        // 同名的一組另外處理（要看同一頁出現幾次）
        int added = 0;
        Dictionary<DiscoveryItem, (MatchResult Match, List<(string Text, bool HasCard)> Rows)> variantRows = [];
        // 依價值排列的頁面：同名的列用上下鄰居的功績直接判斷是組裡的哪一個（同名的各個功績都不同）
        bool byValue = IsSortedByValue(picks);
        for (int i = 0; i < picks.Count; i++)
        {
            (ListRow row, string text, MatchResult match) = picks[i];
            if (match.IsMatch && match.IsVariant)
            {
                // 記住這一個同名列的上下鄰居，分辨是不是之前看過的那一個
                // 只用已確定的鄰居（Excel 的寫法）：同一個鄰居讀錯成兩種寫法時，才不會被當成兩個不同的
                RegisterOccurrence(match.Variants,
                    i > 0 && picks[i - 1].Match.IsMatch ? picks[i - 1].Match.Item!.Name : "",
                    i + 1 < picks.Count && picks[i + 1].Match.IsMatch ? picks[i + 1].Match.Item!.Name : "");
            }
            if (match.IsMatch && match.IsVariant && byValue && PickVariantByMerit(picks, i, match.Variants) is DiscoveryItem exact)
            {
                if (AddVariantExact(category, text, row.HasCard, match, exact))
                {
                    added++;
                }
                continue;
            }
            if (row.Readings.Count == 0 && !match.IsMatch)
            {
                // 讀不出來也推斷不出來：記下它在哪兩個發現物之間，讓使用者自己去遊戲裡看
                string above = i > 0 ? NameOf(picks[i - 1]) : "";
                string below = i + 1 < picks.Count ? NameOf(picks[i + 1]) : "";
                if (AddUnreadable(category, above, below))
                {
                    added++;
                }
                continue;
            }
            if (match.IsMatch && match.IsVariant)
            {
                DiscoveryItem first = match.Variants[0];
                if (!variantRows.TryGetValue(first, out (MatchResult Match, List<(string Text, bool HasCard)> Rows) group))
                {
                    group = (match, new List<(string Text, bool HasCard)>());
                    variantRows[first] = group;
                }
                group.Rows.Add((text, row.HasCard));
            }
            else if (AddResult(category, text, row.HasCard, match))
            {
                added++;
            }
        }
        foreach ((MatchResult match, List<(string Text, bool HasCard)> rows) in variantRows.Values)
        {
            added += AddVariants(category, match, rows);
        }
        int listed = _matched.Values.Count(f => f.Category == category);
        int total = 0;
        if (_book is not null && _book.Categories.TryGetValue(category, out List<DiscoveryItem>? items))
        {
            total = items.Count;
        }
        SetStatus($"{category}{how}：這一頁讀到 {snapshot.Rows.Count} 筆，新出現 {added} 筆。這個分類目前對到 {listed}／{total} 個。");
        UpdateSummary();
    }

    // 一列的名稱（對得到 Excel 就用 Excel 的寫法，否則用讀到的）
    private static string NameOf((ListRow Row, string Text, MatchResult Match) pick)
        => pick.Match.Item?.Name ?? pick.Text;

    /// <summary>加入一筆「有字但讀不出來」的列。同一個位置（上下兩個發現物一樣）已經有了就回傳 false。</summary>
    private bool AddUnreadable(string category, string above, string below)
    {
        string key = category + "\n?" + above + "|" + below;
        if (_unmatched.ContainsKey(key) || _resolvedKeys.Contains(key))
        {
            return false;
        }
        string note = (above, below) switch
        {
            ("", "") => "位置不明",
            ("", _) => $"在「{below}」上面",
            (_, "") => $"在「{above}」下面",
            _ => $"在「{above}」和「{below}」之間",
        };
        Found found = new()
        {
            Category = category, ScreenText = "（讀不出來）", Match = MatchResult.None(0),
            Unreadable = true, Note = note, UnmatchedKey = key, Above = above, Below = below,
        };
        _unmatched[key] = found;
        AddView(found);
        return true;
    }

    // 同一列用不同倍率讀到好幾種結果時，挑對得上而且最像的那個
    private (string Text, MatchResult Match) PickBestReading(string category, ListRow row)
    {
        string bestText = row.Text;
        MatchResult? best = null;
        foreach (string text in row.Readings)
        {
            MatchResult m = _matcher!.Match(category, text);
            if (best is null || (m.IsMatch && !best.IsMatch) || (m.IsMatch == best.IsMatch && m.Score > best.Score))
            {
                best = m;
                bestText = text;
            }
        }
        return (bestText, best ?? MatchResult.None(0));
    }

    /// <summary>
    /// 用同一頁的其他列，推斷「不確定」「找不到」的列（例如漏讀了「鰭」，「帶背的飛龍骨架」跟「帶背刺」「帶背鰭」一樣像）：
    ///   1. 同一頁的每一列都是不同的發現物：別列已經確定的，不會是這一列（同名的一組除外）
    ///   2. 清單照名稱的字元編碼排序時（預設的排序；按 ⇅ 換過排序就不是），這一列一定排在上下已確定的兩列之間
    /// 排除之後只剩一個、或第一名明顯領先，就當成確定。新確定的列可以再幫忙推斷別列，所以最多做 3 輪。
    /// </summary>
    private void ResolveWithinPage(string category, List<(ListRow Row, string Text, MatchResult Match)> picks)
    {
        if (_matcher is null)
        {
            return;
        }
        for (int pass = 0; pass < 3; pass++)
        {
            HashSet<DiscoveryItem> claimed = [.. picks
                .Where(p => p.Match.IsMatch && !p.Match.IsVariant)
                .Select(p => p.Match.Item!)];
            List<(int Index, string Name)> anchors = [.. picks
                .Where(p => p.Match.IsMatch)
                .Select(p => (p.Row.Index, p.Match.Item!.Name))];
            // 已確定的列至少 3 個、而且照編碼由小到大，才當成這一頁是照名稱排序
            bool sorted = anchors.Count >= 3
                          && anchors.Zip(anchors.Skip(1)).All(z => string.CompareOrdinal(z.First.Name, z.Second.Name) <= 0);

            bool changed = false;
            for (int i = 0; i < picks.Count; i++)
            {
                (ListRow row, string text, MatchResult match) = picks[i];
                if (match.IsMatch)
                {
                    continue;
                }
                string? lower = sorted ? anchors.Where(a => a.Index < row.Index).Select(a => a.Name).LastOrDefault() : null;
                string? upper = sorted ? anchors.Where(a => a.Index > row.Index).Select(a => a.Name).FirstOrDefault() : null;

                // 整列讀不出來：照名稱排序、上下都有確定的列，而且中間只有一個還沒對到的發現物，就是它
                if (row.Readings.Count == 0)
                {
                    if (lower is not null && upper is not null)
                    {
                        List<IReadOnlyList<DiscoveryItem>> between = [.. _matcher.Between(category, lower, upper)
                            .Where(g => !(g.Count == 1 && claimed.Contains(g[0])))];
                        if (between.Count == 1)
                        {
                            IReadOnlyList<DiscoveryItem> only = between[0];
                            // 相似度 0 表示是推斷出來的（畫面上顯示「推斷」）
                            picks[i] = (row, "", new MatchResult(only[0], 0, false, only.Count > 1 ? only : []));
                            changed = true;
                        }
                    }
                    continue;
                }

                // 每種倍率讀到的結果都列出候選，同一個候選取最高分
                Dictionary<IReadOnlyList<DiscoveryItem>, (double Score, string Text)> merged = [];
                foreach (string reading in row.Readings)
                {
                    foreach ((IReadOnlyList<DiscoveryItem> items, double score) in _matcher.Candidates(category, reading))
                    {
                        if (!merged.TryGetValue(items, out (double Score, string Text) old) || score > old.Score)
                        {
                            merged[items] = (score, reading);
                        }
                    }
                }
                List<(IReadOnlyList<DiscoveryItem> Items, double Score, string Text)> candidates = [.. merged
                    .Select(kv => (kv.Key, kv.Value.Score, kv.Value.Text))
                    .Where(c => !(c.Key.Count == 1 && claimed.Contains(c.Key[0])))
                    .Where(c => lower is null || string.CompareOrdinal(c.Key[0].Name, lower) >= 0)
                    .Where(c => upper is null || string.CompareOrdinal(c.Key[0].Name, upper) <= 0)
                    .OrderByDescending(c => c.Score)];
                if (candidates.Count == 0 || (candidates.Count > 1 && candidates[0].Score - candidates[1].Score < NameMatcher.MinLead))
                {
                    continue;
                }
                (IReadOnlyList<DiscoveryItem> best, double bestScore, string bestText) = candidates[0];
                picks[i] = (row, bestText, new MatchResult(best[0], bestScore, false, best.Count > 1 ? best : []));
                changed = true;
            }
            if (!changed)
            {
                break;
            }
        }
    }

    // 比對用的名稱拿掉結尾數字（「圓犁頭鰩2」→「圓犁頭鰩」），判斷鄰居是不是同一組
    private static string BaseKey(string name) => Regex.Replace(NameMatcher.Normalize(name), @"[0-9]+$", "");

    /// <summary>
    /// 記下同名組裡的一列（上下鄰居的名稱）。和之前看過的任何一個有共同的鄰居，就是同一個；
    /// 都沒有就是新的一個。捲動時鄰居不會變（變的只是在畫面上的位置），到了頁面邊緣也還有一邊的鄰居對得上。
    /// 鄰居是同一組的（照名稱排列時同名的排在一起）不算，兩邊都沒有鄰居時無法分辨，不記。
    /// </summary>
    private void RegisterOccurrence(IReadOnlyList<DiscoveryItem> variants, string above, string below)
    {
        string groupBase = BaseKey(variants[0].Name);
        string[] neighbors = [above, below];
        HashSet<string> keys = [.. neighbors
            .Where(n => n != "" && n != "（讀不出來）" && BaseKey(n) != groupBase)
            .Select(NameMatcher.Normalize)];
        if (keys.Count == 0)
        {
            return;
        }
        if (!_variantOccurrences.TryGetValue(variants[0], out List<HashSet<string>>? list))
        {
            list = [];
            _variantOccurrences[variants[0]] = list;
        }
        List<HashSet<string>> hits = [.. list.Where(o => o.Overlaps(keys))];
        if (hits.Count == 0)
        {
            list.Add(keys);
            return;
        }
        // 對到一個以上（這次的鄰居把之前分開記的兩筆接起來了）：併成一筆
        hits[0].UnionWith(keys);
        foreach (HashSet<string> extra in hits.Skip(1))
        {
            hits[0].UnionWith(extra);
            list.Remove(extra);
        }
    }

    // 已確定（不是同名的一組）的列：給判斷排序、功績範圍用
    private static bool IsAnchor((ListRow Row, string Text, MatchResult Match) p)
        => p.Match.IsMatch && !p.Match.IsVariant && p.Match.Item is { Merit: > 0 };

    /// <summary>
    /// 這一頁是不是「依價值排列」：已確定的列至少 4 個，功績由高到低（可以相等）。
    /// 照名稱排列時功績是亂的，不會符合。
    /// </summary>
    private static bool IsSortedByValue(List<(ListRow Row, string Text, MatchResult Match)> picks)
    {
        List<int> merits = [.. picks.Where(IsAnchor).Select(p => p.Match.Item!.Merit)];
        return merits.Count >= 4 && merits.Zip(merits.Skip(1)).All(z => z.First >= z.Second);
    }

    /// <summary>
    /// 依價值排列時，同名的列是組裡的哪一個：功績要介於上一個、下一個已確定的列之間。
    /// 只有一個符合時回傳它；判斷不出來（上下都沒有已確定的列、或符合的不只一個）回傳 null。
    /// </summary>
    private static DiscoveryItem? PickVariantByMerit(List<(ListRow Row, string Text, MatchResult Match)> picks, int index,
        IReadOnlyList<DiscoveryItem> variants)
    {
        int? above = null, below = null;
        for (int j = index - 1; j >= 0 && above is null; j--)
        {
            if (IsAnchor(picks[j]))
            {
                above = picks[j].Match.Item!.Merit;
            }
        }
        for (int j = index + 1; j < picks.Count && below is null; j++)
        {
            if (IsAnchor(picks[j]))
            {
                below = picks[j].Match.Item!.Merit;
            }
        }
        if (above is null && below is null)
        {
            return null;
        }
        // 由高到低：上一列的功績 ≥ 這一列 ≥ 下一列（只知道一邊時，另一邊不限）
        int high = above ?? int.MaxValue;
        int low = below ?? int.MinValue;
        List<DiscoveryItem> fit = [.. variants.Where(v => v.Merit <= high && v.Merit >= low)];
        return fit.Count == 1 ? fit[0] : null;
    }

    /// <summary>加入一筆已經確定是同名組裡哪一個的列（依功績判斷的）。已經有了就回傳 false。</summary>
    private bool AddVariantExact(string category, string text, bool hasCard, MatchResult match, DiscoveryItem item)
    {
        if (_matched.TryGetValue(item, out Found? existing))
        {
            if (hasCard && !existing.HasCard && !existing.Manual)
            {
                existing.HasCard = true;
                UpdateRow(existing, resetCheck: false);
            }
            return false;
        }
        // 之前用「同名，請確認」先記下的那一筆（還不知道是哪一個）：就是這一個，改過去，不要多加一筆
        Found? unsure = _matched.Values.FirstOrDefault(f => f.VariantUnsure && !f.Manual && f.Item is not null && match.Variants.Contains(f.Item));
        if (unsure is not null)
        {
            DetachFound(unsure);
            unsure.Assigned = item;
            unsure.VariantUnsure = false;
            unsure.HasCard |= hasCard;
            _matched[item] = unsure;
            UpdateRow(unsure, resetCheck: true);
            return false;
        }

        Found found = new()
        {
            Category = category, ScreenText = text, Match = match, HasCard = hasCard, Assigned = item,
        };
        _matched[item] = found;
        AddView(found);
        return true;
    }

    /// <summary>
    /// 同名的一組（例如「聖母加冠1」「聖母加冠2」，遊戲上都叫「聖母加冠」）——照名稱排列的頁面用：
    /// 照名稱排列時同名的排在一起，同一頁看到幾個，就是組裡前幾個都發現了。全部看到才確定；只看到一部分時分不出是哪一個，標成「同名，請確認」。
    /// 回傳新加入幾筆。
    /// </summary>
    private int AddVariants(string category, MatchResult match, List<(string Text, bool HasCard)> rows)
    {
        IReadOnlyList<DiscoveryItem> variants = match.Variants;
        DiscoveryItem first = variants[0];
        // 看過幾個：同一頁同時看到的個數（照名稱排列時同名的排在一起），
        // 或是用鄰居分辨出來的個數（依價值排列時同名的分散在不同頁），取大的
        _variantSeen.TryGetValue(first, out int before);
        int occurrences = _variantOccurrences.TryGetValue(first, out List<HashSet<string>>? occ) ? occ.Count : 0;
        int seen = Math.Min(variants.Count, Math.Max(before, Math.Max(rows.Count, occurrences)));
        _variantSeen[first] = seen;
        bool complete = seen == variants.Count;

        // 有卡片的資訊：第 k 個看到的對應組裡第 k 個（手動指定過的不動）
        for (int k = 0; k < Math.Min(rows.Count, variants.Count); k++)
        {
            if (rows[k].HasCard && _matched.TryGetValue(variants[k], out Found? existing) && !existing.Manual && !existing.HasCard)
            {
                existing.HasCard = true;
                UpdateRow(existing, resetCheck: false);
            }
        }

        // 用「個數」判斷要不要補：同一頁看到幾個，清單上這一組就要有幾筆。
        // 已經在清單上的（自動對到的、手動指定的）和使用者移除的都算，
        // 否則把「水鹿」手動改成「水鹿2」之後，下一次讀到又會補一個「水鹿」回來。
        _variantSuppressed.TryGetValue(first, out int suppressed);
        int present = variants.Count(v => _matched.ContainsKey(v)) + suppressed;
        int added = 0;
        int rowIndex = 0;
        foreach (DiscoveryItem item in variants)
        {
            if (present >= seen)
            {
                break;
            }
            if (_matched.ContainsKey(item))
            {
                continue;
            }
            (string text, bool hasCard) = rows[Math.Min(rowIndex++, rows.Count - 1)];
            Found found = new()
            {
                Category = category, ScreenText = text, Match = match, HasCard = hasCard,
                Assigned = item, VariantUnsure = !complete,
            };
            _matched[item] = found;
            AddView(found);
            present++;
            added++;
        }

        // 這次才看齊全部的話，之前標成「同名，請確認」的改成確定（手動指定的本來就確定）
        foreach (DiscoveryItem item in variants)
        {
            if (_matched.TryGetValue(item, out Found? f) && f.VariantUnsure && complete && !f.Manual)
            {
                f.VariantUnsure = false;
                UpdateRow(f, resetCheck: true);
            }
        }
        return added;
    }

    private void AddView(Found found)
    {
        found.View = new ListViewItem(["", "", "", "", "", ""]) { Tag = found };
        _list.Items.Add(found.View);
        UpdateRow(found, resetCheck: true);
        found.View.EnsureVisible();
    }

    /// <summary>加入一筆讀到的結果。已經有了就回傳 false。</summary>
    private bool AddResult(string category, string screenText, bool hasCard, MatchResult match)
    {
        if (_resolvedKeys.Contains(TextKey(category, screenText)))
        {
            return false; // 這個文字手動處理過了
        }
        Found found;
        if (match.IsMatch)
        {
            if (_matched.TryGetValue(match.Item!, out Found? existing))
            {
                if (hasCard && !existing.HasCard)
                {
                    existing.HasCard = true;
                    UpdateRow(existing, resetCheck: false);
                }
                return false;
            }
            found = new Found { Category = category, ScreenText = screenText, Match = match, HasCard = hasCard };
            _matched[match.Item!] = found;
        }
        else
        {
            string key = TextKey(category, screenText);
            if (_unmatched.ContainsKey(key))
            {
                return false;
            }
            found = new Found { Category = category, ScreenText = screenText, Match = match, HasCard = hasCard, UnmatchedKey = key };
            _unmatched[key] = found;
        }

        AddView(found);
        return true;
    }

    private static string TextKey(string category, string screenText) => category + "\n" + NameMatcher.Normalize(screenText);

    // ── 手動指定、移除 ──

    private Found? SelectedFound => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Found : null;

    private void AssignSelected()
    {
        if (_book is null || _matcher is null || SelectedFound is not Found f)
        {
            return;
        }
        string screen = f.Unreadable ? "" : f.ScreenText;
        HashSet<DiscoveryItem> onList = [.. _matched.Keys.Where(k => _matched[k] != f)];
        using ItemPickerForm dlg = new(_book, f.Category, screen, Character, onList, f.Above, f.Below);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedItem is not DiscoveryItem item)
        {
            return;
        }

        if (_matched.TryGetValue(item, out Found? other) && other != f)
        {
            // 同一個發現物已經在清單上：這一列是重複的
            if (MessageBox.Show(this, $"「{item.Name}」已經在清單上了。\n\n要移除這一列嗎？", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            {
                RemoveFound(f);
            }
            return;
        }

        RememberResolved(f);
        // 同名的一組裡的一列改指定到組外：這一組少了一筆，記下來免得下次讀到又補回來
        if (f.Match.IsVariant && !f.Match.Variants.Contains(item))
        {
            Suppress(f.Match.Variants[0]);
        }
        DetachFound(f);
        f.Assigned = item;
        f.Category = item.Category;
        f.Manual = true;
        f.VariantUnsure = false;
        _matched[item] = f;
        UpdateRow(f, resetCheck: true);
        UpdateSummary();
    }

    private void RemoveSelected()
    {
        if (SelectedFound is Found f)
        {
            RemoveFound(f);
        }
    }

    private void Suppress(DiscoveryItem first)
    {
        _variantSuppressed.TryGetValue(first, out int n);
        _variantSuppressed[first] = n + 1;
    }

    private void RemoveFound(Found f)
    {
        if (f.Match.IsVariant)
        {
            Suppress(f.Match.Variants[0]); // 移除同名的一列：下次讀到不要補回來
        }
        RememberResolved(f);
        DetachFound(f);
        if (f.View is not null)
        {
            _list.Items.Remove(f.View);
        }
        UpdateSummary();
    }

    // 記住這一列手動處理過（同名的一組例外：組裡每一列的文字都一樣，記住的話其他列也讀不到了）
    private void RememberResolved(Found f)
    {
        if (f.Unreadable && f.UnmatchedKey is not null)
        {
            _resolvedKeys.Add(f.UnmatchedKey);
        }
        else if (!f.Match.IsVariant)
        {
            _resolvedKeys.Add(TextKey(f.Category, f.ScreenText));
        }
    }

    // 從「對到」「對不到」兩邊的紀錄拿掉（清單上的那一列不動）
    private void DetachFound(Found f)
    {
        if (f.UnmatchedKey is not null)
        {
            _unmatched.Remove(f.UnmatchedKey);
            f.UnmatchedKey = null;
        }
        foreach (DiscoveryItem key in _matched.Where(kv => kv.Value == f).Select(kv => kv.Key).ToList())
        {
            _matched.Remove(key);
        }
    }

    private void UpdateRow(Found f, bool resetCheck)
    {
        if (f.View is not ListViewItem view)
        {
            return;
        }
        int character = Character;
        view.SubItems[0].Text = f.Category;
        view.SubItems[1].Text = f.ScreenText == "" ? "（讀不出來）" : f.ScreenText;
        view.SubItems[2].Text = f.Item?.Name ?? f.Note;
        view.SubItems[3].Text = f.Item is null ? ""
            : f.Manual ? "手動"
            : f.Match.Score == 0 ? "推斷"
            : f.Match.Score.ToString("0%", CultureInfo.InvariantCulture);
        view.SubItems[4].Text = f.HasCard ? "✔" : "";

        string status;
        Color color;
        bool check;
        if (f.Manual && f.Item is not null)
        {
            bool marked = f.Item.Marked[character];
            status = marked ? "已經打勾" : "新增（手動指定）";
            color = marked ? UiColors.PickForList(Color.Gray, Color.DarkGray) : UiColors.PickForList(Color.SeaGreen, Color.LightGreen);
            check = !marked;
        }
        else if (f.Unreadable)
        {
            status = "讀不出來，請手動確認";
            color = UiColors.PickForList(Color.Firebrick, Color.FromArgb(255, 140, 110));
            check = false;
        }
        else if (f.Item is null)
        {
            status = "找不到";
            color = UiColors.PickForList(Color.Firebrick, Color.FromArgb(255, 140, 110));
            check = false;
        }
        else if (f.Item.Marked[character])
        {
            status = "已經打勾";
            color = UiColors.PickForList(Color.Gray, Color.DarkGray);
            check = false;
        }
        else if (f.VariantUnsure)
        {
            status = $"同名，請確認（共 {f.Match.Variants.Count} 個）";
            color = UiColors.PickForList(Color.DarkOrange, Color.Orange);
            check = false;
        }
        else if (f.Match.Ambiguous)
        {
            status = "不確定（請確認）";
            color = UiColors.PickForList(Color.DarkOrange, Color.Orange);
            check = false;
        }
        else
        {
            status = "新增";
            color = UiColors.PickForList(Color.SeaGreen, Color.LightGreen);
            check = true;
        }
        view.SubItems[5].Text = status;
        view.ForeColor = color;
        if (resetCheck)
        {
            view.Checked = check;
        }
    }

    private void ClearResults()
    {
        _matched.Clear();
        _unmatched.Clear();
        _variantSeen.Clear();
        _resolvedKeys.Clear();
        _variantSuppressed.Clear();
        _variantOccurrences.Clear();
        _list.Items.Clear();
        _processedMask = null;
        UpdateSummary();
    }

    // 存下整個遊戲畫面（檔名帶縮放倍率），之後可以用「用截圖檔測試…」重讀，也方便回報問題。
    // 另外存辨識前處理好的標題、清單（-title.png、-list.png）和辨識結果（.txt）
    private async Task SaveScreenAsync()
    {
        if (_busy)
        {
            SetStatus("正在辨識，請稍後再按一次。");
            return;
        }
        _busy = true; // 存檔時計時器不要同時辨識
        try
        {
            await SaveScreenCoreAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveScreenCoreAsync()
    {
        if (!_window.TryGetClientArea(out _, out double scale))
        {
            MessageBox.Show(this, "找不到遊戲視窗。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using Bitmap? client = _window.CaptureClient();
        if (client is null)
        {
            MessageBox.Show(this, "截不到遊戲畫面。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "screens");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"discovery-{DateTime.Now:yyyyMMdd-HHmmss}-s{scale.ToString("0.##", CultureInfo.InvariantCulture)}.png");
            client.Save(path, ImageFormat.Png);
            if (_reader is not null)
            {
                string stem = Path.ChangeExtension(path, null);
                (PreparedImage title, PreparedImage list) = _reader.PrepareForDebug(client, scale);
                using (Bitmap b = title.ToBitmap())
                {
                    b.Save(stem + "-title.png", ImageFormat.Png);
                }
                using (Bitmap b = list.ToBitmap())
                {
                    b.Save(stem + "-list.png", ImageFormat.Png);
                }
                ListSnapshot snapshot = await _reader.ReadAsync(client, scale);
                List<string> lines =
                [
                    $"縮放 {scale}、辨識放大 {_reader.OcrScale} 倍、語言 {_reader.LanguageName}",
                    $"標題：「{snapshot.TitleText}」（{snapshot.TitleScale} 倍）→ 分類：{_matcher?.MatchCategory(snapshot.TitleText) ?? "（對不到）"}",
                    $"從清單推測的分類：{_matcher?.GuessCategory([.. snapshot.Rows.Where(r => r.Text != "").Select(r => r.Text)]) ?? "（推測不出來）"}",
                ];
                // 每一列：辨識讀到的原文 → 比對結果（套用錯字對照表、同一頁推斷之後，和實際讀取時一樣）
                string? category = _matcher?.MatchCategory(snapshot.TitleText)
                                   ?? _matcher?.GuessCategory([.. snapshot.Rows.Where(r => r.Text != "").Select(r => r.Text)]);
                List<(ListRow Row, string Text, MatchResult Match)> picks = [];
                if (category is not null)
                {
                    foreach (ListRow row in snapshot.Rows)
                    {
                        (string text, MatchResult match) = PickBestReading(category, row);
                        picks.Add((row, text, match));
                    }
                    ResolveWithinPage(category, picks);
                }
                foreach (ListRow row in snapshot.Rows)
                {
                    string read = row.Readings.Count == 0 ? "（讀不出來）" : "「" + string.Join("」「", row.Readings) + "」";
                    string result = "";
                    int at = picks.FindIndex(p => p.Row == row);
                    if (at >= 0)
                    {
                        MatchResult m = picks[at].Match;
                        result = m.Item is null ? " → 找不到"
                            : $" → {m.Item.Name}（{(m.Score == 0 ? "推斷" : m.Score.ToString("0%", CultureInfo.InvariantCulture))}" +
                              $"{(m.Ambiguous ? "，不確定" : "")}{(m.IsVariant ? "，同名的一組" : "")}）";
                    }
                    lines.Add($"第 {row.Index + 1} 列：{read}{(row.HasCard ? "（有卡片）" : "")}{result}");
                }
                File.WriteAllLines(stem + ".txt", lines);
            }
            SetStatus("已存下遊戲畫面：" + path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "存檔失敗：\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // 讀「存下遊戲畫面」存的截圖。檔名裡的 -s1.5 是縮放倍率；沒有的話用寬度 ÷ 800 推算
    private async Task TestFromFileAsync()
    {
        if (_reader is null || _matcher is null)
        {
            MessageBox.Show(this, "請先選 Excel 檔案（也需要繁體中文的文字辨識）。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using OpenFileDialog dlg = new()
        {
            Title = "選擇遊戲畫面截圖（用「存下遊戲畫面」存的）",
            Filter = "PNG 圖片 (*.png)|*.png",
            Multiselect = true,
            InitialDirectory = Path.Combine(AppContext.BaseDirectory, "screens"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        if (_busy)
        {
            SetStatus("正在辨識，請稍後再按一次。");
            return;
        }
        _busy = true; // 測試時計時器不要同時辨識
        try
        {
            await TestFilesAsync(dlg.FileNames);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task TestFilesAsync(string[] files)
    {
        foreach (string path in files)
        {
            try
            {
                using Bitmap bmp = new(path);
                Match m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"-s(\d+(?:\.\d+)?)$");
                double scale = m.Success
                    ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                    : Math.Max(1, bmp.Width / 800.0);
                await ProcessFrameAsync(bmp, scale, live: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{Path.GetFileName(path)} 讀不出來：\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // ── 畫面更新 ──

    private void SetStatus(string text)
    {
        _lblStatus.Text = text;
    }

    private void UpdateButtons()
    {
        bool ready = _book is not null && _reader is not null;
        _btnStart.Enabled = ready;
        _btnStart.Text = _timer.Enabled ? "停止讀取" : "開始讀取";
        _btnWrite.Enabled = _book is not null;
        _btnSaveScreen.Enabled = true;
        _btnTestFile.Enabled = ready;
    }

    private void UpdateSummary()
    {
        int character = Character;
        int newCount = _matched.Values.Count(f => f.Item is not null && !f.Item.Marked[character] && !f.VariantUnsure);
        int already = _matched.Values.Count(f => f.Item is not null && f.Item.Marked[character]);
        int unsure = _unmatched.Values.Count(f => f.Item is not null)
                     + _matched.Values.Count(f => f.Item is not null && !f.Item.Marked[character] && f.VariantUnsure);
        int missing = _unmatched.Values.Count(f => f.Item is null);
        _lblSummary.Text = $"新增 {newCount}　已經打勾 {already}　不確定 {unsure}　找不到 {missing}";
    }

    // ── 視窗位置 ──

    private void RestoreWindow()
    {
        if (_settings.WindowWidth <= 0)
        {
            return;
        }
        Rectangle bounds = new(_settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight);
        // 存的位置在目前的螢幕上看得到才用（例如拔掉外接螢幕後）
        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }
    }

    // 標題列：版本，置頂時加註
    private void UpdateTitle()
    {
        Text = $"發現物同步 v{Application.ProductVersion.Split('+')[0]}{(TopMost ? "（置頂，F1 取消）" : "")}";
    }

    // F1：切換視窗置頂（遊戲在前面時也看得到清單和狀態）
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F1)
        {
            TopMost = !TopMost;
            _settings.TopMost = TopMost;
            UpdateTitle();
            SetStatus(TopMost ? "視窗置頂：開（再按 F1 取消）。注意不要擋住遊戲的清單。" : "視窗置頂：關。");
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // F1 預設會觸發「說明」事件，這裡已經拿來切換置頂，不要再跳出說明
    protected override void OnHelpRequested(HelpEventArgs hevent)
    {
        hevent.Handled = true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WindowX = bounds.X;
        _settings.WindowY = bounds.Y;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.Character = Character;
        _settings.Save();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
