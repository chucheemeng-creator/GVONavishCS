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

        /// <summary>讀到這一列時，這一頁是不是依價值排列（手動指定時才用功績範圍縮小候選）。</summary>
        public bool PageByValue { get; init; }

        /// <summary>同名組的列：它是看過的哪一個（用上下鄰居辨識的身分），還不知道時是 null。</summary>
        public Occurrence? Occurrence { get; set; }

        /// <summary>這一筆（原本屬於同名組）已經從組的筆數裡扣掉了（被移除、或手動改指定到組外）。</summary>
        public bool VariantSuppressed { get; set; }

        /// <summary>扣的時候它沒有身分，所以也算進了「沒有身分被移除」的筆數（扣回來時要一起扣回）。</summary>
        public bool AnonymousCounted { get; set; }

        public string Note { get; init; } = "";

        /// <summary>
        /// 對不到、讀不出來的列：上一列、下一列「已確定」的名稱（Excel 的寫法）。
        /// 用來判斷之後讀對的列是不是同一個位置（是的話這一筆就過時了），手動指定時也用來捲到附近。
        /// </summary>
        public string Above { get; init; } = "";
        public string Below { get; init; } = "";

        public DiscoveryItem? Item => Assigned ?? Match.Item;
    }

    private readonly AppSettings _settings;

    // 錯字對照表（獨立的檔案，程式開著也可以改，改了自動重新讀）
    private readonly ConfusionTable _confusions;
    private readonly string? _confusionProblem;
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
    private readonly Dictionary<DiscoveryItem, List<Occurrence>> _variantOccurrences = [];
    // 使用者移除過的同名列的身分：之後再讀到同一個，不要加回來
    private readonly HashSet<Occurrence> _removedOccurrences = [];
    // 同名的一組 → 使用者移除的「沒有身分」的筆數（沒有鄰居的列，補的時候要算進去）
    private readonly Dictionary<DiscoveryItem, int> _anonymousRemoved = [];
    // 同名組的一列（AddVariants 用）
    private sealed record VariantRow(string Text, bool HasCard, Occurrence? Occurrence);

    /// <summary>
    /// 同名組裡看過的一列的身分：它上一列、下一列已確定的名稱（Excel 的寫法）。
    /// 捲動時鄰居不會變，只是到了頁面邊緣只看得到一邊；之後看到另一邊時補上。
    /// 用參考相等當作身分（同一個物件＝同一列）。
    /// </summary>
    private sealed class Occurrence
    {
        public string Above { get; set; } = "";
        public string Below { get; set; } = "";

        // 和「上一列是 above、下一列是 below」是不是同一列：規則和 SamePosition 一樣
        public bool Matches(string above, string below)
        {
            bool aboveKnown = above != "" && Above != "";
            bool belowKnown = below != "" && Below != "";
            if (aboveKnown && belowKnown)
            {
                return Above == above && Below == below;
            }
            if (aboveKnown)
            {
                return Above == above;
            }
            return belowKnown && Below == below;
        }
    }

    private bool _busy;
    // 視窗正在關閉：還在跑的辨識回來後不要再碰畫面
    private bool _closing;
    // 寫入 Excel、手動指定進行中（包括等辨識結束、對話框開著的時候）：
    // 這段期間不能再開始另一個操作，也不能手動開始／停止讀取（否則結束時的「恢復讀取」會蓋掉使用者的選擇）
    private bool _operationPending;
    // 一頁一頁加入清單時暫停重繪（AddView 不各自捲動，結束後只捲到最後加入的那一筆）
    private bool _batching;
    private ListViewItem? _lastAdded;
    // 上一次截圖、上一次辨識時，畫面上字的位置（判斷捲動停了沒、讀過了沒）
    private byte[]? _lastMask;
    private byte[]? _processedMask;
    // 失敗重試：哪一個畫面、已經重試幾次
    private byte[]? _retryMask;
    private int _retries;

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
        (_confusions, bool migrated, _confusionProblem) = ConfusionTable.Load(settings.OcrConfusions);
        if (migrated && settings.OcrConfusions is not null)
        {
            // 舊版設定檔裡的錯字對照表已經搬到獨立的檔案：從設定檔拿掉
            settings.OcrConfusions = null;
            settings.Save();
        }
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
        _btnBrowse.Click += (_, _) =>
        {
            if (!_operationPending)
            {
                BrowseExcel();
            }
        };
        top.Controls.Add(_btnBrowse, 2, 0);

        // 第 2 列：人物、按鈕
        top.Controls.Add(new Label { Text = "人物：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) }, 0, 1);
        FlowLayoutPanel buttons = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        _cboCharacter.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboCharacter.Width = 140;
        _cboCharacter.SelectedIndexChanged += (_, _) => OnCharacterChanged();
        buttons.Controls.Add(_cboCharacter);
        SetupButton(_btnStart, "開始讀取", (_, _) => ToggleReading());
        SetupButton(_btnWrite, "寫入 Excel", async (_, _) => await WriteExcelAsync());
        SetupButton(_btnClear, "清除結果", (_, _) =>
        {
            if (!_operationPending)
            {
                ClearResults();
            }
        });
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
        // 勾選改變時更新「新增」「不確定」的數字（一次加很多列時，最後才一起更新）
        _list.ItemChecked += (_, _) =>
        {
            if (!_batching)
            {
                UpdateSummary();
            }
        };
        _list.GridLines = false;
        _list.Columns.Add("分類", 80);
        _list.Columns.Add("遊戲畫面上的名稱", 200);
        _list.Columns.Add("Excel 上的名稱", 200);
        _list.Columns.Add("相似度", 60, HorizontalAlignment.Right);
        _list.Columns.Add("卡片", 45, HorizontalAlignment.Center);
        _list.Columns.Add("狀態", 120);
        ContextMenuStrip menu = new();
        ToolStripMenuItem assignItem = new("指定 Excel 發現物…", null, async (_, _) => await AssignSelectedAsync()) { ShortcutKeyDisplayString = "Enter" };
        ToolStripMenuItem removeItem = new("移除這一筆", null, (_, _) => RemoveSelected()) { ShortcutKeyDisplayString = "Delete" };
        ToolStripMenuItem copyItem = new("複製遊戲畫面上的名稱", null, (_, _) => CopyScreenText()) { ShortcutKeyDisplayString = "Ctrl+C" };
        menu.Items.AddRange([assignItem, removeItem, new ToolStripSeparator(), copyItem]);
        menu.Opening += (_, e) =>
        {
            bool has = SelectedFound is not null;
            assignItem.Enabled = has && _book is not null && !_operationPending;
            removeItem.Enabled = has && !_operationPending;
            copyItem.Enabled = SelectedScreenTexts().Count > 0;
            e.Cancel = !has;
        };
        _list.ContextMenuStrip = menu;
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                _ = AssignSelectedAsync();
            }
            else if (e.KeyCode == Keys.Delete)
            {
                RemoveSelected();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode is Keys.C or Keys.Insert)
            {
                CopyScreenText();
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
        if (_confusionProblem is not null)
        {
            MessageBox.Show(this, _confusionProblem + "\n\n先只用內建的錯字對照表，改好存檔後會自動重新讀取。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            _matcher = new NameMatcher(book, _confusions.Map);
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
        RefreshAllRows();
        UpdateSummary();
    }

    private async Task WriteExcelAsync()
    {
        if (_book is null || !TryBeginOperation())
        {
            return;
        }
        try
        {
            await WriteExcelCoreAsync();
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task WriteExcelCoreAsync()
    {
        if (_book is null)
        {
            return;
        }
        // 先停止讀取、等正在跑的那一輪辨識結束，再抓要寫入的名單：
        // 不然確認視窗開著時，背景辨識還可能改動清單，畫面上看到的和實際寫入的會不一樣
        (bool wasRunning, bool completed) = await PauseReadingAsync();
        try
        {
            if (_closing || IsDisposed)
            {
                return;
            }
            if (!completed)
            {
                ShowStillBusy(); // 背景辨識還在跑，可能還會改動清單：這次不寫
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
                    $"要幫「{name}」在 {items.Count} 個發現物打勾嗎？\n\n寫入前會先把原檔備份到 Excel 所在資料夾的 backup 資料夾。\n寫入時 Excel 必須是關閉的。",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                WriteResult result = _book.WriteMarks(character, items);
                RefreshAllRows();
                UpdateSummary();
                string message = $"已寫入 {result.Written.Count} 筆。\n\n原檔備份為：\nbackup\\{Path.GetFileName(result.BackupPath)}";
                if (result.Skipped.Count > 0)
                {
                    // 讀檔之後 Excel 被改過（插列、排序、換版本）：列號對不上名稱的沒有寫，請重新選一次 Excel 再寫
                    message += $"\n\n有 {result.Skipped.Count} 筆沒有寫入：Excel 在讀取之後被改過（例如插列、排序），" +
                               "那一列已經不是這個發現物了。請按「選擇…」重新讀取 Excel，再讀一次清單後寫入。\n\n" +
                               string.Join("\n", result.Skipped.Take(10).Select(i => $"・{i.Category}／{i.Name}")) +
                               (result.Skipped.Count > 10 ? $"\n……還有 {result.Skipped.Count - 10} 筆" : "");
                }
                MessageBox.Show(this, message, Text, MessageBoxButtons.OK,
                    result.Skipped.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (ExcelWriteException ex)
            {
                // 記憶體從磁碟重新讀一次，和檔案保持一致（讀不出來就維持原狀）
                _book.ReloadMarks();
                RefreshAllRows();
                UpdateSummary();
                string backupNote = ex.FileMayBeModified
                    ? $"存檔途中出錯，原檔可能已經部分改動（甚至損壞）。\n請關閉程式，用寫入前的備份蓋回原檔：\n{ex.BackupPath}"
                    : ex.BackupPath == ""
                        ? "（還沒開始備份，原檔沒有被改動）"
                        : $"原檔沒有被改動。寫入前的備份在：\n{ex.BackupPath}";
                MessageBox.Show(this, $"寫入失敗：\n\n{ex.Message}\n\n{backupNote}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }
        finally
        {
            ResumeReading(wasRunning);
        }
    }

    /// <summary>
    /// 停止讀取，並等正在跑的那一輪辨識結束（最多等 10 秒）。
    /// WasRunning：原本是不是在讀取中（給 ResumeReading 用）；Completed：是不是真的等到辨識結束了。
    /// 沒等到時呼叫端要取消這次操作（短名稱讀不出來時會多辨識好幾輪，有可能超過 10 秒）。
    /// </summary>
    private async Task<(bool WasRunning, bool Completed)> PauseReadingAsync()
    {
        bool wasRunning = _timer.Enabled;
        _timer.Stop();
        for (int i = 0; i < 200 && _busy; i++)
        {
            await Task.Delay(50);
        }
        return (wasRunning, !_busy);
    }

    // 等不到辨識結束時的提示
    private void ShowStillBusy()
    {
        if (!_closing && !IsDisposed)
        {
            MessageBox.Show(this, "辨識還在進行中，請稍等一下再試一次。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ResumeReading(bool wasRunning)
    {
        if (wasRunning && !_closing && !IsDisposed)
        {
            _lastMask = null; // 重新確認畫面停下來了再讀
            _timer.Start();
        }
    }

    // ── 讀遊戲畫面 ──

    private void ToggleReading()
    {
        if (_operationPending)
        {
            return; // 寫入或指定進行中：結束時會自己恢復原本的讀取狀態
        }
        if (_timer.Enabled)
        {
            _timer.Stop();
            SetStatus("已停止讀取。");
        }
        else
        {
            _lastMask = null;
            _processedMask = null;
            _retries = 0;
            _retryMask = null;
            _timer.Start();
            SetStatus("讀取中…在遊戲裡慢慢捲動清單，每次停下來就會讀一次。");
        }
        UpdateButtons();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (_busy || _closing || IsDisposed || _reader is null || _matcher is null)
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
            AllowRetry();
            if (!_closing && !IsDisposed)
            {
                SetStatus("讀取時發生錯誤：" + ex.Message);
            }
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
        // 錯字對照表改過（程式開著時修改、存檔）：換成新的，目前這一頁重新讀一次
        if (_confusions.ReloadIfChanged() is string confusionMessage)
        {
            _matcher.SetConfusions(_confusions.Map);
            _processedMask = null;
            SetStatus(confusionMessage);
        }
        byte[] mask = _reader.TextMask(client, scale);
        // 「畫面停了沒、讀過了沒、重試幾次」只給連續讀取用：用截圖檔測試（live＝false）不看也不改，
        // 否則之後開始讀取時，畫面剛好和測試的截圖一樣就會被當成讀過了
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
            _processedMask = mask;
            if (!ReferenceEquals(mask, _retryMask) && !DiscoveryListReader.SameText(mask, _retryMask))
            {
                _retryMask = mask; // 換了一個畫面：重試次數重新算
                _retries = 0;
            }
        }

        NameMatcher matcher = _matcher;
        ListSnapshot snapshot = await _reader.ReadAsync(client, scale, t => matcher.MatchCategory(t) is not null);
        if (_closing || IsDisposed)
        {
            return; // 辨識的時候視窗關掉了
        }
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
            AllowRetry();
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
        // 這一頁是怎麼排序的（依名稱、依價值、看不出來）；用來推斷不確定的列和同名的發現物
        SortMode mode = DetectSort(picks);
        ResolveWithinPage(category, picks, mode == SortMode.ByName);

        // 加入、移除清單的這一段暫停重繪：一頁最多加 10 筆，不要每加一筆就重繪、捲動一次
        _list.BeginUpdate();
        _batching = true;
        try
        {
            AddPage(category, picks, how, snapshot.Rows.Count);
        }
        finally
        {
            _batching = false;
            _list.EndUpdate();
            // 只捲到最後加入的那一筆
            if (_lastAdded is not null && _list.Items.Contains(_lastAdded))
            {
                _lastAdded.EnsureVisible();
            }
            _lastAdded = null;
        }
    }

    // 把一頁的結果加進清單（ProcessFrameAsync 用，呼叫時清單已經暫停重繪）
    private void AddPage(string category, List<(ListRow Row, string Text, MatchResult Match)> picks, string how, int rowCount)
    {
        // 同名的一組另外處理（要看同一頁出現幾次）
        int added = 0;
        Dictionary<DiscoveryItem, (MatchResult Match, List<VariantRow> Rows)> variantRows = [];
        // 依價值排列的頁面：同名的列用上下鄰居的功績直接判斷是組裡的哪一個（同名的各個功績都不同）
        _pageSort = DetectSort(picks); // 推斷完再判斷一次，已確定的列比較多
        bool byValue = _pageSort == SortMode.ByValue;
        for (int i = 0; i < picks.Count; i++)
        {
            (ListRow row, string text, MatchResult match) = picks[i];
            Occurrence? occurrence = null;
            if (match.IsMatch && match.IsVariant)
            {
                // 記住這一個同名列的上下鄰居，分辨是不是之前看過的那一個
                // 只用已確定的鄰居（Excel 的寫法）：同一個鄰居讀錯成兩種寫法時，才不會被當成兩個不同的
                occurrence = RegisterOccurrence(match.Variants, ConfidentName(picks, i - 1), ConfidentName(picks, i + 1));
                if (occurrence is not null && _removedOccurrences.Contains(occurrence))
                {
                    continue; // 使用者移除過的那一個
                }
            }
            if (match.IsMatch && match.IsVariant && byValue && PickVariantByMerit(picks, i, match.Variants) is DiscoveryItem exact)
            {
                if (AddVariantExact(category, text, row.HasCard, match, exact, occurrence))
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
                if (AddUnreadable(category, above, below, ConfidentName(picks, i - 1), ConfidentName(picks, i + 1)))
                {
                    added++;
                }
                continue;
            }
            if (match.IsMatch && match.IsVariant)
            {
                DiscoveryItem first = match.Variants[0];
                if (!variantRows.TryGetValue(first, out (MatchResult Match, List<VariantRow> Rows) group))
                {
                    group = (match, new List<VariantRow>());
                    variantRows[first] = group;
                }
                group.Rows.Add(new VariantRow(text, row.HasCard, occurrence));
            }
            else if (AddResult(category, text, row.HasCard, match, ConfidentName(picks, i - 1), ConfidentName(picks, i + 1)))
            {
                added++;
            }
        }
        foreach ((MatchResult match, List<VariantRow> rows) in variantRows.Values)
        {
            added += AddVariants(category, match, rows);
        }
        RemoveStale(category, picks);
        int listed = _matched.Values.Count(f => f.Category == category);
        int total = 0;
        if (_book is not null && _book.Categories.TryGetValue(category, out List<DiscoveryItem>? items))
        {
            total = items.Count;
        }
        string sortText = _pageSort switch
        {
            SortMode.ByValue => "依價值排列",
            SortMode.ByName => "依名稱排列",
            _ => "排序看不出來",
        };
        SetStatus($"{category}{how}（{sortText}）：這一頁讀到 {rowCount} 筆，新出現 {added} 筆。這個分類目前對到 {listed}／{total} 個。");
        UpdateSummary();
    }

    // 這一輪失敗（分類判斷不出來、發生例外）：同一個畫面再給 2 次機會，不要記成已處理
    // （不設上限的話，停在別的畫面時會每 0.4 秒辨識一次）
    private void AllowRetry()
    {
        if (_processedMask is null)
        {
            return; // 用截圖檔測試時沒有記「已處理」，不用重試
        }
        if (_retries < 2)
        {
            _retries++;
            _processedMask = null;
        }
    }

    // 第 index 列已確定的話，它的 Excel 名稱；沒有這一列、還不確定、或是同名的一組時是 ""
    // （同名組的每一列都先對到組裡第一個，好幾列的鄰居會叫同一個名字，不能拿來當位置的依據）
    private static string ConfidentName(List<(ListRow Row, string Text, MatchResult Match)> picks, int index)
        => index >= 0 && index < picks.Count && picks[index].Match.IsMatch && !picks[index].Match.IsVariant
            ? picks[index].Match.Item!.Name : "";

    /// <summary>
    /// f 和「上一列是 above、下一列是 below」是不是清單上同一個位置。
    /// 兩邊都有資料時兩邊都要相同；只有一邊有資料（頁面邊緣、鄰居還不確定）時看那一邊；都沒有時不算。
    /// </summary>
    private static bool SamePosition(Found f, string above, string below)
    {
        bool aboveKnown = above != "" && f.Above != "";
        bool belowKnown = below != "" && f.Below != "";
        if (aboveKnown && belowKnown)
        {
            return f.Above == above && f.Below == below;
        }
        if (aboveKnown)
        {
            return f.Above == above;
        }
        return belowKnown && f.Below == below;
    }

    /// <summary>
    /// 清掉過時的「找不到」「不確定」「讀不出來」：這一頁確定讀到的列，和它們在同一個位置（見 SamePosition），
    /// 表示那一筆就是這一列之前讀錯（或讀不出來）的結果。手動處理過的不動。
    /// </summary>
    private void RemoveStale(string category, List<(ListRow Row, string Text, MatchResult Match)> picks)
    {
        if (_unmatched.Count == 0)
        {
            return;
        }
        for (int i = 0; i < picks.Count; i++)
        {
            if (!picks[i].Match.IsMatch)
            {
                continue;
            }
            string above = ConfidentName(picks, i - 1);
            string below = ConfidentName(picks, i + 1);
            if (above == "" && below == "")
            {
                continue;
            }
            List<Found> stale = [.. _unmatched.Values.Where(f => !f.Manual && f.Category == category && SamePosition(f, above, below))];
            foreach (Found f in stale)
            {
                DetachFound(f);
                if (f.View is not null)
                {
                    _list.Items.Remove(f.View);
                }
            }
        }
    }

    // 一列的名稱（顯示用）：對得到 Excel 就用 Excel 的寫法；同名的一組用遊戲上的寫法（拿掉結尾數字），否則用讀到的
    private static string NameOf((ListRow Row, string Text, MatchResult Match) pick)
        => pick.Match.Item is DiscoveryItem item
            ? (pick.Match.IsVariant ? NameMatcher.SortKey(item.Name) : item.Name)
            : pick.Text;

    /// <summary>
    /// 加入一筆「有字但讀不出來」的列。above、below 是顯示用的上下列名稱，confidentAbove、confidentBelow 是已確定的。
    /// 同一個位置已經有了（捲到頁面邊緣時只剩一邊的鄰居，也算同一個位置）就回傳 false。
    /// </summary>
    private bool AddUnreadable(string category, string above, string below, string confidentAbove, string confidentBelow)
    {
        // 索引鍵用已確定的鄰居（同名組的列不算，免得夾在同名列旁邊的兩筆撞在一起）；兩邊都沒有時才用顯示用的名稱
        string key = confidentAbove != "" || confidentBelow != ""
            ? category + "\n?" + confidentAbove + "|" + confidentBelow
            : category + "\n?~" + above + "|" + below;
        if (_unmatched.ContainsKey(key) || _resolvedKeys.Contains(key)
            || _unmatched.Values.Any(f => f.Category == category && SamePosition(f, confidentAbove, confidentBelow)))
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
            Unreadable = true, Note = note, UnmatchedKey = key, Above = confidentAbove, Below = confidentBelow,
            PageByValue = _pageSort == SortMode.ByValue,
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
            MatchResult m = _matcher!.Match(category, text, row.TextWidth);
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
    /// 注意：只排除「這一頁」已確定的，不排除前幾頁對到的（_matched）。捲動時上下頁大部分重疊，
    /// 這一列很可能就是前幾頁已經對到的那一個、只是這次讀錯了；排除它會被推成另一個沒發現的發現物並預設打勾。
    /// </summary>
    private void ResolveWithinPage(string category, List<(ListRow Row, string Text, MatchResult Match)> picks, bool sortedByName)
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
                .Select(p => (p.Row.Index, Name: NameMatcher.SortKey(p.Match.Item!.Name)))]; // 比較順序用拿掉結尾數字的名稱
            // 這一頁照名稱排序（DetectSort 判斷的），而且新確定的列也沒有違反，才用上下列的順序排除候選
            bool sorted = sortedByName
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

                // 整列讀不出來（多半是一、兩個字的短名稱）：用字數、排序的上下界、這一頁已確定的排除候選，只剩一個就是它。
                // 例如昆蟲類一個字的只有「蠶」；量不到字寬時，要照名稱排序而且上下都有確定的列才推斷
                if (row.Readings.Count == 0)
                {
                    if (row.TextWidth > 0 || (lower is not null && upper is not null))
                    {
                        List<IReadOnlyList<DiscoveryItem>> between = [.. _matcher.Between(category, lower, upper)
                            .Where(g => !(g.Count == 1 && claimed.Contains(g[0])))
                            .Where(g => NameMatcher.FitsWidth(g, row.TextWidth))];
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
                // 以組裡第一個發現物當索引鍵（不靠清單物件本身的參考相等）
                Dictionary<DiscoveryItem, (IReadOnlyList<DiscoveryItem> Items, double Score, string Text)> merged = [];
                foreach (string reading in row.Readings)
                {
                    foreach ((IReadOnlyList<DiscoveryItem> items, double score) in _matcher.Candidates(category, reading, row.TextWidth))
                    {
                        if (!merged.TryGetValue(items[0], out (IReadOnlyList<DiscoveryItem> Items, double Score, string Text) old)
                            || score > old.Score)
                        {
                            merged[items[0]] = (items, score, reading);
                        }
                    }
                }
                List<(IReadOnlyList<DiscoveryItem> Items, double Score, string Text)> candidates = [.. merged.Values
                    .Where(c => !(c.Items.Count == 1 && claimed.Contains(c.Items[0])))
                    .Where(c => lower is null || string.CompareOrdinal(NameMatcher.SortKey(c.Items[0].Name), lower) >= 0)
                    .Where(c => upper is null || string.CompareOrdinal(NameMatcher.SortKey(c.Items[0].Name), upper) <= 0)
                    .OrderByDescending(c => c.Score)];
                if (candidates.Count == 0 || (candidates.Count > 1 && candidates[0].Score - candidates[1].Score < NameMatcher.MinLead))
                {
                    continue;
                }
                // 讀到的字被剩下的好幾個名稱完整包含（「鳳蝶」之於「黑鳳蝶」「玫瑰鳳蝶」）：漏的是哪幾個字分不出來，不推斷
                string read = candidates[0].Text;
                if (candidates.Count > 1 && NameMatcher.IsContainedIn(read, candidates[0].Items[0].Name)
                    && candidates.Skip(1).Any(c => NameMatcher.IsContainedIn(read, c.Items[0].Name)))
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

    /// <summary>
    /// 找出同名組裡這一列的身分（上下鄰居是 above、below）：和之前看過的某一個是同一列就回傳它（順便補上新看到的那一邊），
    /// 都不是就記成新的一個。above、below 都是已確定的名稱（ConfidentName），兩邊都沒有時無法辨識，回傳 null。
    /// 比對規則和 SamePosition 一樣：兩邊都知道時兩邊都要相同，只共用一個鄰居的兩列不會被當成同一列。
    /// </summary>
    private Occurrence? RegisterOccurrence(IReadOnlyList<DiscoveryItem> variants, string above, string below)
    {
        if (above == "" && below == "")
        {
            return null;
        }
        if (!_variantOccurrences.TryGetValue(variants[0], out List<Occurrence>? list))
        {
            list = [];
            _variantOccurrences[variants[0]] = list;
        }
        Occurrence? hit = list.FirstOrDefault(o => o.Matches(above, below));
        if (hit is null)
        {
            hit = new Occurrence { Above = above, Below = below };
            list.Add(hit);
            return hit;
        }
        // 補上之前不知道的那一邊
        if (hit.Above == "")
        {
            hit.Above = above;
        }
        if (hit.Below == "")
        {
            hit.Below = below;
        }
        return hit;
    }

    // 已確定（不是同名的一組）的列：給判斷排序、功績範圍用。
    // 要求功績 > 0：功績是 0 表示 Excel 那一欄是空的或讀不出數字，不能拿來比大小
    // （目前的 Excel 3,931 個發現物功績都大於 0；新版 Excel 若出現合法的 0 功績要回來看這裡）
    private static bool IsAnchor((ListRow Row, string Text, MatchResult Match) p)
        => p.Match.IsMatch && !p.Match.IsVariant && p.Match.Item is { Merit: > 0 };

    private enum SortMode { Unknown, ByValue, ByName }

    // 記住的排序方式：遊戲的排序是一個設定（按 ⇅ 才會換），有一頁證據充分就記住，之後證據不足的頁面沿用
    private SortMode _sortMode = SortMode.Unknown;
    // 最近一頁判斷出的排序（狀態列顯示；讀不出來的列記下來，手動指定時才知道能不能用功績範圍）
    private SortMode _pageSort = SortMode.Unknown;

    /// <summary>
    /// 判斷這一頁的排序（依價值：功績由高到低；依名稱：字元編碼由小到大）。
    ///   - 已確定的列至少 4 個、而且只符合其中一種：證據充分，記住這個排序方式
    ///   - 至少 2 個、只符合其中一種：這一頁就是這種排序
    ///   - 證據不足（例如發現物少、這一頁只有一兩個已確定的列，兩種都符合）：沒有矛盾就沿用記住的排序方式
    /// 兩種都符合時不下判斷，免得誤判（列數少時常常兩種都符合）。
    /// 用剛開始玩的玩家模擬過（只有 1%～3% 的發現物）：判斷不出來的時候有，但沒有判斷錯的。
    /// </summary>
    private SortMode DetectSort(List<(ListRow Row, string Text, MatchResult Match)> picks, bool remember = true)
    {
        // 兩種排序用同一組已確定的列（不含同名組：同名組的列都先對到組裡第一個，功績、名稱都不代表那一列）
        List<(ListRow Row, string Text, MatchResult Match)> anchors = [.. picks.Where(IsAnchor)];
        List<int> merits = [.. anchors.Select(p => p.Match.Item!.Merit)];
        List<string> names = [.. anchors.Select(p => NameMatcher.SortKey(p.Match.Item!.Name))];
        bool valueOrdered = merits.Zip(merits.Skip(1)).All(z => z.First >= z.Second);
        bool nameOrdered = names.Zip(names.Skip(1)).All(z => string.CompareOrdinal(z.First, z.Second) <= 0);

        bool value4 = valueOrdered && merits.Count >= 4, name4 = nameOrdered && names.Count >= 4;
        if (remember && value4 != name4)
        {
            _sortMode = value4 ? SortMode.ByValue : SortMode.ByName;
        }

        bool value2 = valueOrdered && merits.Count >= 2, name2 = nameOrdered && names.Count >= 2;
        if (value2 != name2)
        {
            SortMode page = value2 ? SortMode.ByValue : SortMode.ByName;
            // 這一頁只符合另一種排序：可能在遊戲裡按 ⇅ 換了排序，記住的不可靠了，清掉
            if (remember && _sortMode != SortMode.Unknown && _sortMode != page)
            {
                _sortMode = SortMode.Unknown;
            }
            return page;
        }
        // 證據不足：這一頁沒有違反記住的排序就沿用
        return _sortMode switch
        {
            SortMode.ByValue when valueOrdered => SortMode.ByValue,
            SortMode.ByName when nameOrdered => SortMode.ByName,
            _ => SortMode.Unknown,
        };
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
    private bool AddVariantExact(string category, string text, bool hasCard, MatchResult match, DiscoveryItem item,
        Occurrence? occurrence)
    {
        // 這一列（同一個身分）已經在清單上、而且確定了（手動指定、或看齊了）：尊重它，什麼都不動。
        // 要放在最前面：否則下面會先把佔住這個發現物的那一筆挪走，然後才發現不用加
        if (occurrence is not null
            && _matched.Values.FirstOrDefault(f => f.Occurrence == occurrence && (f.Manual || !f.VariantUnsure)) is Found confirmed)
        {
            // 這次才看到卡片的話補上卡片標記（手動指定的不動；不影響勾選）
            if (hasCard && !confirmed.HasCard && !confirmed.Manual)
            {
                confirmed.HasCard = true;
                UpdateRow(confirmed, resetCheck: false);
            }
            return false;
        }
        if (_matched.TryGetValue(item, out Found? existing))
        {
            // 這個發現物已經在清單上。同名組的列一開始是依序填空位，那一筆不一定就是這一列，要先看身分：
            if (occurrence is not null && existing.Occurrence == occurrence)
            {
                // 就是這一列：確定下來
                if (!existing.Manual && (existing.VariantUnsure || (hasCard && !existing.HasCard)))
                {
                    existing.VariantUnsure = false;
                    existing.HasCard |= hasCard;
                    UpdateRow(existing, resetCheck: true);
                }
                return false;
            }
            if (existing.Manual || !existing.VariantUnsure)
            {
                return false; // 手動指定的、已經確定的：不動
            }
            // 那一筆是別的列（或還沒有身分），只是先佔了這個位置：把它移到組裡還空著的位置（仍然「請確認」），
            // 這個發現物留給這一列。組裡沒有空位時那一筆不動（這一列就不另外加）
            DiscoveryItem? free = match.Variants.FirstOrDefault(v => !_matched.ContainsKey(v));
            if (free is null)
            {
                return false;
            }
            DetachFound(existing);
            existing.Assigned = free;
            _matched[free] = existing;
            UpdateRow(existing, resetCheck: true);
            // 往下走：為這一列新增一筆確定的
        }
        // 同一個身分之前用「同名，請確認」先記下的那一筆：就是這一列，改成功績判斷出來的那一個。
        // 只改同一個身分的；沒有身分的那一筆可能是另一列，不能拿來改（改錯會把另一隻變成已確定並預設寫入）
        Found? unsure = occurrence is null ? null
            : _matched.Values.FirstOrDefault(f => f.VariantUnsure && !f.Manual && f.Occurrence == occurrence);
        if (unsure is not null)
        {
            DetachFound(unsure);
            unsure.Assigned = item;
            unsure.VariantUnsure = false;
            unsure.HasCard |= hasCard;
            unsure.Occurrence ??= occurrence;
            _matched[item] = unsure;
            UpdateRow(unsure, resetCheck: true);
            return false;
        }

        Found found = new()
        {
            Category = category, ScreenText = text, Match = match, HasCard = hasCard, Assigned = item, Occurrence = occurrence,
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
    private int AddVariants(string category, MatchResult match, List<VariantRow> rows)
    {
        IReadOnlyList<DiscoveryItem> variants = match.Variants;
        DiscoveryItem first = variants[0];
        // 同一頁同時看到幾個（照名稱排列時同名的排在一起）：同時看到全部才算確定。
        // 刻意不用「清單上這一組已經湊滿」當作確定：清單上的筆數有一部分是靠上下鄰居辨識的身分加的，
        // 鄰居認錯時同一隻會被算成兩隻；湊滿就確定的話，多算一隻會讓整組（包括沒發現的那一個）都預設打勾。
        // 實際上很少一直卡在「請確認」：依名稱排列時同名的連在一起，正常捲動一定有一頁同時看到全部；
        // 依價值排列時同名的分散在各頁，但每一隻會由 AddVariantExact 用上下鄰居的功績各自確定。
        _variantSeen.TryGetValue(first, out int before);
        int together = Math.Max(before, Math.Min(rows.Count, variants.Count));
        _variantSeen[first] = together;
        bool complete = together >= variants.Count;
        _variantSuppressed.TryGetValue(first, out int suppressed);
        _anonymousRemoved.TryGetValue(first, out int anonymousRemoved);

        // 每一個實際看到的列對應清單上的一筆，不會因為鄰居數出幾個就一次補滿：
        //   - 有身分（上下鄰居）的列：同一個身分已經有一筆就不再加，新的身分就加一筆
        //   - 沒有身分的列：沒有身分的筆數不超過「同一頁同時看到幾個」
        int added = 0;
        foreach (VariantRow row in rows)
        {
            List<Found> members = [.. variants.Where(_matched.ContainsKey).Select(v => _matched[v])];
            int present = members.Count + suppressed;

            if (row.Occurrence is not null)
            {
                // 同一個身分已經在清單上（可能被手動改成組外的發現物，所以整個清單找）
                Found? linked = _matched.Values.FirstOrDefault(f => f.Occurrence == row.Occurrence);
                if (linked is not null)
                {
                    if (row.HasCard && !linked.HasCard && !linked.Manual)
                    {
                        linked.HasCard = true;
                        UpdateRow(linked, resetCheck: false);
                    }
                    continue;
                }
                // 新的身分：加一筆新的「同名，請確認」。不要寫進之前沒有身分的那一筆——那可能是另一列，
                // 寫進去會讓這一列靜靜漏掉。同一列先沒鄰居、後來有鄰居，最多暫時變成兩筆「請確認」（少算一隻比較嚴重）
                if (present >= variants.Count)
                {
                    continue;
                }
            }
            else if (members.Count(f => f.Occurrence is null) + anonymousRemoved >= together)
            {
                continue; // 沒有鄰居的列：沒有身分的筆數（含移除的）已經和這一頁同時看到的一樣多了
            }

            DiscoveryItem? item = variants.FirstOrDefault(v => !_matched.ContainsKey(v));
            if (item is null)
            {
                continue;
            }
            Found found = new()
            {
                Category = category, ScreenText = row.Text, Match = match, HasCard = row.HasCard,
                Assigned = item, VariantUnsure = !complete, Occurrence = row.Occurrence,
            };
            _matched[item] = found;
            AddView(found);
            added++;
        }

        // 這次才同時看齊全部的話，之前標成「同名，請確認」的改成確定（手動指定的本來就確定）
        if (complete)
        {
            foreach (DiscoveryItem item in variants)
            {
                if (_matched.TryGetValue(item, out Found? f) && f.VariantUnsure && !f.Manual)
                {
                    f.VariantUnsure = false;
                    UpdateRow(f, resetCheck: true);
                }
            }
        }
        return added;
    }

    private void AddView(Found found)
    {
        found.View = new ListViewItem(["", "", "", "", "", ""]) { Tag = found };
        _list.Items.Add(found.View);
        UpdateRow(found, resetCheck: true);
        if (_batching)
        {
            _lastAdded = found.View; // 批次加入時，結束後只捲到最後一筆
        }
        else
        {
            found.View.EnsureVisible();
        }
    }

    /// <summary>加入一筆讀到的結果。已經有了就回傳 false。</summary>
    private bool AddResult(string category, string screenText, bool hasCard, MatchResult match,
        string confidentAbove = "", string confidentBelow = "")
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
            // 同一個位置已經有一筆對不到的（同一列讀錯成不同的字）：不要重複
            if (_unmatched.ContainsKey(key)
                || _unmatched.Values.Any(f => f.Category == category && SamePosition(f, confidentAbove, confidentBelow)))
            {
                return false;
            }
            found = new Found
            {
                Category = category, ScreenText = screenText, Match = match, HasCard = hasCard, UnmatchedKey = key,
                Above = confidentAbove, Below = confidentBelow,
            };
            _unmatched[key] = found;
        }

        AddView(found);
        return true;
    }

    private static string TextKey(string category, string screenText) => category + "\n" + NameMatcher.Normalize(screenText);

    // ── 手動指定、移除 ──

    private Found? SelectedFound => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Found : null;

    private async Task AssignSelectedAsync()
    {
        if (_book is null || _matcher is null || SelectedFound is not Found f || !TryBeginOperation())
        {
            return;
        }
        try
        {
            await AssignSelectedCoreAsync(f);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task AssignSelectedCoreAsync(Found f)
    {
        // 選的時候暫停讀取，並等正在跑的那一輪辨識結束：不然背景讀到的結果可能改動這一筆
        (bool wasRunning, bool completed) = await PauseReadingAsync();
        try
        {
            if (!completed)
            {
                ShowStillBusy();
                return;
            }
            AssignCore(f);
        }
        finally
        {
            ResumeReading(wasRunning);
        }
    }

    private void AssignCore(Found f)
    {
        if (_book is null || _closing || IsDisposed || f.View is null || !_list.Items.Contains(f.View))
        {
            return;
        }
        string screen = f.Unreadable ? "" : f.ScreenText;
        // 已經在清單上的：對到的，加上「不確定」但已經被勾選（使用者確認過）的
        HashSet<DiscoveryItem> onList = [.. _matched.Keys.Where(k => _matched[k] != f),
            .. _unmatched.Values.Where(u => u != f && u.Item is not null && u.View is { Checked: true }).Select(u => u.Item!)];
        DiscoveryItem item;
        using (ItemPickerForm dlg = new(_book, f.Category, screen, Character, onList, f.Above, f.Below, f.PageByValue, f.Item))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedItem is not DiscoveryItem picked)
            {
                return;
            }
            item = picked;
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
        // 原本屬於同名組的列：改指定到組外就從組的筆數扣掉，改回組內就扣回來（每一筆最多扣一次）
        SetVariantSuppressed(f, f.Match.IsVariant && !f.Match.Variants.Contains(item));
        DetachFound(f);
        f.Assigned = item;
        f.Category = item.Category;
        f.Manual = true;
        f.VariantUnsure = false;
        _matched[item] = f;
        UpdateRow(f, resetCheck: true);
        UpdateSummary();
    }

    // 選取的列（可以多選）在遊戲畫面上讀到的文字；讀不出來的列沒有文字，不算
    private List<string> SelectedScreenTexts()
        => [.. _list.SelectedItems.Cast<ListViewItem>()
            .Select(v => v.Tag as Found)
            .Where(f => f is not null && !f.Unreadable && f.ScreenText != "")
            .Select(f => f!.ScreenText)];

    /// <summary>
    /// 複製選取的列「遊戲畫面上的名稱」（辨識讀到的原文，多選時一行一個）。
    /// 常讀錯的字可以直接貼進設定檔的錯字對照表，不用猜是哪個字（例如「冫」）。
    /// </summary>
    private void CopyScreenText()
    {
        List<string> texts = SelectedScreenTexts();
        if (texts.Count == 0)
        {
            return;
        }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, texts));
            SetStatus(texts.Count == 1 ? $"已複製：{texts[0]}" : $"已複製 {texts.Count} 筆遊戲畫面上的名稱。");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetStatus("複製失敗：剪貼簿正在被其他程式使用，請再試一次。");
        }
    }

    private void RemoveSelected()
    {
        if (_operationPending)
        {
            return;
        }
        if (SelectedFound is Found f)
        {
            RemoveFound(f);
        }
    }

    /// <summary>
    /// 設定這一筆（原本屬於同名組）有沒有從組的筆數扣掉。狀態有變才改計數：
    /// 沒扣 → 扣：組的筆數 +1（沒有身分的話「沒有身分被移除」也 +1）；扣 → 沒扣：扣回來。
    /// 同一筆改指定到組外、再移除，只會扣一次；改回組內會恢復。
    /// </summary>
    private void SetVariantSuppressed(Found f, bool suppressed)
    {
        if (!f.Match.IsVariant || f.VariantSuppressed == suppressed)
        {
            return;
        }
        DiscoveryItem first = f.Match.Variants[0];
        int delta = suppressed ? 1 : -1;
        if (suppressed)
        {
            f.AnonymousCounted = f.Occurrence is null;
        }
        _variantSuppressed[first] = Math.Max(0, _variantSuppressed.GetValueOrDefault(first) + delta);
        if (f.AnonymousCounted)
        {
            _anonymousRemoved[first] = Math.Max(0, _anonymousRemoved.GetValueOrDefault(first) + delta);
        }
        if (!suppressed)
        {
            f.AnonymousCounted = false;
        }
        f.VariantSuppressed = suppressed;
    }

    private void RemoveFound(Found f)
    {
        if (f.Match.IsVariant)
        {
            SetVariantSuppressed(f, true); // 移除同名的一列：下次讀到不要補回來（之前改指定到組外時已經扣過，不會重複扣）
            if (f.Occurrence is not null)
            {
                _removedOccurrences.Add(f.Occurrence);
            }
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

    // 全部重新判斷狀態（勾選回到預設）：換人物、寫入 Excel 後用。清單可能有幾百筆，暫停重繪一次更新完
    private void RefreshAllRows()
    {
        _list.BeginUpdate();
        try
        {
            foreach (Found f in _matched.Values.Concat(_unmatched.Values))
            {
                UpdateRow(f, resetCheck: true);
            }
        }
        finally
        {
            _list.EndUpdate();
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
        else if (f.Match.Score == 0)
        {
            // 讀不出來、用排序推斷出來的：推斷依賴「這一頁是怎麼排序的」的判斷，預設不勾，請確認
            status = "推斷，請確認";
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
        _removedOccurrences.Clear();
        _anonymousRemoved.Clear();
        _retries = 0;
        _retryMask = null;
        _list.Items.Clear();
        _processedMask = null;
        _lastMask = null;
        _sortMode = SortMode.Unknown; // 重新開始時重新判斷排序（使用者可能已經按 ⇅ 換了排序）
        UpdateSummary();
    }

    // 存下整個遊戲畫面（檔名帶縮放倍率），之後可以用「用截圖檔測試…」重讀，也方便回報問題。
    // 另外存辨識前處理好的標題、清單（-title.png、-list.png）和辨識結果（.txt）
    private async Task SaveScreenAsync()
    {
        if (_operationPending)
        {
            return;
        }
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
                if (_closing || IsDisposed)
                {
                    return;
                }
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
                    // 除錯輸出不改變記住的排序方式
                    ResolveWithinPage(category, picks, DetectSort(picks, remember: false) == SortMode.ByName);
                }
                foreach (ListRow row in snapshot.Rows)
                {
                    string read = (row.Readings.Count == 0 ? "（讀不出來）" : "「" + string.Join("」「", row.Readings) + "」")
                        + (row.TextWidth > 0
                            ? $"（寬 {row.TextWidth.ToString("0", CultureInfo.InvariantCulture)}，約 {((row.TextWidth + 2) / NameMatcher.FullWidthCharPixels).ToString("0.#", CultureInfo.InvariantCulture)} 個字）"
                            : "");
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
        if (_operationPending)
        {
            return;
        }
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
            if (_closing || IsDisposed)
            {
                return;
            }
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
        bool idle = !_operationPending;
        _btnStart.Enabled = ready && idle;
        _btnStart.Text = _timer.Enabled ? "停止讀取" : "開始讀取";
        _btnWrite.Enabled = _book is not null && idle;
        _btnClear.Enabled = idle;
        _btnBrowse.Enabled = idle;
        _cboCharacter.Enabled = idle;
        _btnSaveScreen.Enabled = idle;
        _btnTestFile.Enabled = ready && idle;
    }

    // 開始一個會等辨識、開對話框的操作（寫入、手動指定）。已經有一個在進行時回傳 false
    private bool TryBeginOperation()
    {
        if (_operationPending)
        {
            return false;
        }
        _operationPending = true;
        UpdateButtons();
        return true;
    }

    private void EndOperation()
    {
        _operationPending = false;
        if (!_closing && !IsDisposed)
        {
            UpdateButtons();
        }
    }

    // 要使用者確認的（同名分不出是哪一個、不確定、用排序推斷的）；手動指定的已經確認過
    private static bool NeedsConfirm(Found f)
        => !f.Manual && (f.VariantUnsure || f.Match.Ambiguous || f.Match.Score == 0);

    private void UpdateSummary()
    {
        int character = Character;
        // 和 UpdateRow 的狀態判斷一致（「不確定」的列在 _unmatched 裡，但 Excel 已經打勾的話狀態是「已經打勾」）
        List<Found> all = [.. _matched.Values, .. _unmatched.Values];
        int already = all.Count(f => f.Item is not null && f.Item.Marked[character]);
        // 新增＝勾選了、會寫入 Excel 的（「不確定」「推斷」的列自己確認後打勾，也算新增）；
        // 不確定＝需要確認、還沒勾的
        int newCount = all.Count(f => f.Item is not null && !f.Item.Marked[character] && f.View is { Checked: true });
        int unsure = all.Count(f => f.Item is not null && !f.Item.Marked[character] && f.View is not { Checked: true } && NeedsConfirm(f));
        int missing = all.Count(f => f.Item is null);
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
        // 還有勾選、但還沒寫入 Excel 的：問一下，免得辛苦讀的結果不見
        int character = Character;
        int pending = _matched.Values.Concat(_unmatched.Values)
            .Count(f => f.View is { Checked: true } && f.Item is not null && !f.Item.Marked[character]);
        if (pending > 0 && e.CloseReason == CloseReason.UserClosing
            && MessageBox.Show(this, $"還有 {pending} 筆勾選的發現物沒有寫入 Excel，關閉後這次讀到的結果會不見。\n\n確定要關閉嗎？",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            e.Cancel = true;
            return;
        }
        _closing = true;
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
