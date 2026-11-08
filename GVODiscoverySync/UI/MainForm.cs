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
using GVODiscoverySync.Recognition;

namespace GVODiscoverySync.UI;

public sealed class MainForm : Form
{

    private readonly AppSettings _settings;

    // 錯字對照表（獨立的檔案，程式開著也可以改，改了自動重新讀）
    private readonly ConfusionTable _confusions;
    private readonly string? _confusionProblem;
    private readonly GameWindow _window = new();
    private readonly DiscoveryListReader? _reader;
    private readonly System.Windows.Forms.Timer _timer = new();

    private DiscoveryBook? _book;
    private NameMatcher? _matcher;
    // 讀取的結果和推斷規則（Recognition/DiscoverySession.cs）；讀了 Excel 才有
    private DiscoverySession? _session;


    private bool _busy;
    // 視窗正在關閉：還在跑的辨識回來後不要再碰畫面
    private bool _closing;
    // 寫入 Excel、手動指定進行中（包括等辨識結束、對話框開著的時候）：
    // 這段期間不能再開始另一個操作，也不能手動開始／停止讀取（否則結束時的「恢復讀取」會蓋掉使用者的選擇）
    private bool _operationPending;
    // 一頁一頁加入清單時暫停重繪（新加的列不各自捲動，結束後只捲到最後加入的那一筆）
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
            _session = new DiscoverySession(book, _matcher)
            {
                Added = OnFoundAdded,
                Updated = UpdateRow,
                Removed = OnFoundRemoved,
            };
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
            List<DiscoveryItem> items = [.. (_session?.All ?? [])
                .Where(f => ViewOf(f) is { Checked: true } && f.Item is not null && !f.Item.Marked[character])
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
        if (_reader is null || _matcher is null || _session is null)
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
        ListSnapshot snapshot = await _reader.ReadAsync(client, scale, t => matcher.MatchCategory(t) is not null, AllExact);
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

        // 加入、移除清單的這一段暫停重繪：一頁最多加 10 筆，不要每加一筆就重繪、捲動一次
        _list.BeginUpdate();
        _batching = true;
        try
        {
            // 每一列挑最像的讀法、判斷排序、同一頁推斷，再加進結果（Recognition/DiscoverySession.cs）
            PageResult page = _session.AddPage(category, snapshot.Rows);
            string sortText = page.PageSort switch
            {
                SortMode.ByValue => "依價值排列",
                SortMode.ByName => "依名稱排列",
                _ => "排序看不出來",
            };
            SetStatus($"{category}{how}（{sortText}）：這一頁讀到 {snapshot.Rows.Count} 筆，新出現 {page.Added} 筆。" +
                      $"這個分類目前對到 {page.Listed}／{page.Total} 個。");
            UpdateSummary();
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

    // 第一個倍率讀到的清單是不是每一列都和 Excel 的名稱完全一樣（而且字數對得上）：是的話不用再換倍率讀。
    // 只看「完全一樣」，不看模糊比對的結果：模糊對到的，換個倍率可能讀得更完整，還是要讀
    private bool AllExact(string title, IReadOnlyList<(string Text, double Width)> rows)
    {
        if (_matcher is not NameMatcher matcher)
        {
            return false;
        }
        string? category = matcher.MatchCategory(title) ?? matcher.GuessCategory([.. rows.Select(r => r.Text)]);
        return category is not null && rows.All(r => matcher.Match(category, r.Text, r.Width) is { IsMatch: true, Score: >= 1 });
    }

    // ── 手動指定、移除 ──

    private Found? SelectedFound => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Found : null;

    private async Task AssignSelectedAsync()
    {
        if (_session is null || SelectedFound is not Found f || !TryBeginOperation())
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
        if (_session is null || _closing || IsDisposed || ViewOf(f) is not ListViewItem view || !_list.Items.Contains(view))
        {
            return;
        }
        string screen = f.Unreadable ? "" : f.ScreenText;
        // 已經在清單上的：對到的，加上「不確定」但已經被勾選（使用者確認過）的
        HashSet<DiscoveryItem> onList = [.. _session.Matched.Keys.Where(k => _session.Matched[k] != f),
            .. _session.Unmatched.Values.Where(u => u != f && u.Item is not null && ViewOf(u) is { Checked: true }).Select(u => u.Item!)];
        DiscoveryItem item;
        // 主視窗置頂（F1）時，選擇視窗也要置頂，不然會被主視窗蓋住、無法操作
        using (ItemPickerForm dlg = new(_session.Book, f.Category, screen, Character, onList, f.Above, f.Below, f.PageByValue, f.Item,
                   f.TextWidth, _session.LastPage)
               {
                   TopMost = TopMost,
               })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedItem is not DiscoveryItem picked)
            {
                return;
            }
            item = picked;
        }

        if (_session.Assign(f, item) is not null)
        {
            // 同一個發現物已經在清單上：這一列是重複的
            if (MessageBox.Show(this, $"「{item.Name}」已經在清單上了。\n\n要移除這一列嗎？", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            {
                RemoveFound(f);
            }
            return;
        }
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

    private void RemoveFound(Found f)
    {
        _session?.Remove(f);
        UpdateSummary();
    }

    // 這一筆在畫面上的那一列
    private static ListViewItem? ViewOf(Found f) => f.View as ListViewItem;

    // 新加了一筆：加一列
    private void OnFoundAdded(Found found)
    {
        ListViewItem view = new(["", "", "", "", "", ""]) { Tag = found };
        found.View = view;
        _list.Items.Add(view);
        UpdateRow(found, resetCheck: true);
        if (_batching)
        {
            _lastAdded = view; // 批次加入時，結束後只捲到最後一筆
        }
        else
        {
            view.EnsureVisible();
        }
    }

    // 一筆移除了：拿掉那一列
    private void OnFoundRemoved(Found found)
    {
        if (ViewOf(found) is ListViewItem view)
        {
            _list.Items.Remove(view);
        }
    }

    // 全部重新判斷狀態（勾選回到預設）：換人物、寫入 Excel 後用。清單可能有幾百筆，暫停重繪一次更新完
    private void RefreshAllRows()
    {
        _list.BeginUpdate();
        try
        {
            foreach (Found f in _session?.All ?? [])
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
        if (ViewOf(f) is not ListViewItem view)
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
        _session?.Clear(); // 排序方式也重新判斷（使用者可能已經按 ⇅ 換了排序）
        _retries = 0;
        _retryMask = null;
        _list.Items.Clear();
        _processedMask = null;
        _lastMask = null;
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
                // 除錯輸出不改變記住的排序方式
                List<(ListRow Row, string Text, MatchResult Match)> picks = category is not null && _session is not null
                    ? _session.Analyze(category, snapshot.Rows, remember: false)
                    : [];
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

    private void UpdateSummary()
    {
        int character = Character;
        // 和 UpdateRow 的狀態判斷一致（「不確定」的列在 Unmatched 裡，但 Excel 已經打勾的話狀態是「已經打勾」）
        List<Found> all = [.. _session?.All ?? []];
        int already = all.Count(f => f.Item is not null && f.Item.Marked[character]);
        // 新增＝勾選了、會寫入 Excel 的（「不確定」「推斷」的列自己確認後打勾，也算新增）；
        // 不確定＝需要確認、還沒勾的
        int newCount = all.Count(f => f.Item is not null && !f.Item.Marked[character] && ViewOf(f) is { Checked: true });
        int unsure = all.Count(f => f.Item is not null && !f.Item.Marked[character] && ViewOf(f) is not { Checked: true } && f.NeedsConfirm);
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
        int pending = (_session?.All ?? [])
            .Count(f => ViewOf(f) is { Checked: true } && f.Item is not null && !f.Item.Marked[character]);
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
