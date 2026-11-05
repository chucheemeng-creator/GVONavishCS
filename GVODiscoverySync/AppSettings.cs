// AppSettings.cs
// 設定檔，存在程式同一個資料夾的 GVODiscoverySync.json
// （不叫 settings.json，和 GVONavish 放在同一個資料夾也不會互相蓋掉）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GVO.Common.IO;
using GVODiscoverySync.Ocr;

namespace GVODiscoverySync;

public class AppSettings
{
    /// <summary>上一次用的 Excel 檔案。</summary>
    public string ExcelPath { get; set; } = "";

    /// <summary>上一次選的人物（0～4）。</summary>
    public int Character { get; set; }

    /// <summary>多久看一次遊戲畫面（毫秒）。</summary>
    public int IntervalMs { get; set; } = 400;

    /// <summary>辨識前放大成遊戲原本像素的幾倍（預設 3；4 倍時實測讀不到分類標題）。</summary>
    public double OcrScale { get; set; } = 3;

    /// <summary>
    /// 文字辨識固定會讀錯的字：讀到的 → 正確的。比對時照原樣、換字後各比一次，取比較像的。
    /// 發現新的固定錯字時，自己在設定檔加一行就好。
    /// 內建的（BuiltInConfusions）每次啟動都會補進來，所以更新程式後新加的內建錯字不用手動加；
    /// 不想用某一組內建的，把它改成對應到自己（例如 "專": "專"）。
    /// </summary>
    public Dictionary<string, string> OcrConfusions { get; set; } = new(BuiltInConfusions);

    /// <summary>內建的錯字對照表。</summary>
    public static readonly Dictionary<string, string> BuiltInConfusions = new()
    {
        ["巑"] = "鑽", // 黃鑽、粉鑽、藍鑽、科依諾爾鑽
        ["專"] = "轉", // 汽轉球 → 專球（漏了偏旁）
        ["降"] = "隆", // 隆派亞長刀 → 降派亞
        ["冫每"] = "海", // 「氵」讀成「冫」：海埔姜 → 冫每埔姜、海洋生物化石 → 冫每洋生物化石
        ["冫少"] = "沙", // 沙漠螞蟻 → 冫少漠螞蟻
    };

    /// <summary>分類標題和清單在遊戲畫面上的位置。</summary>
    public ListLayout Layout { get; set; } = new();

    // ── 視窗 ──
    /// <summary>視窗位置和大小。寬度為 0 表示還沒存過，用預設值。</summary>
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    [JsonIgnore]
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "GVODiscoverySync.json");

    /// <summary>讀設定檔時發生的問題（給主視窗顯示），沒有問題時是 null。</summary>
    [JsonIgnore]
    public string? LoadProblem { get; private set; }

    // 設定檔讀不出來、又沒辦法改名保留時，這次不存檔（免得蓋掉原檔）
    [JsonIgnore]
    private bool SaveDisabled { get; set; }

    private void Normalize()
    {
        Character = Math.Clamp(Character, 0, 4);
        IntervalMs = Math.Clamp(IntervalMs, 100, 5000);
        OcrScale = double.IsFinite(OcrScale) ? Math.Clamp(OcrScale, 1.5, 6) : 3;
        Layout ??= new ListLayout();
        OcrConfusions ??= [];
        // 舊的設定檔沒有新版內建的錯字：補進來（使用者自己加的、改過的不動）
        foreach ((string wrong, string right) in BuiltInConfusions)
        {
            OcrConfusions.TryAdd(wrong, right);
        }
        if (Layout.RowHeight <= 0 || Layout.RowCount <= 0)
        {
            Layout = new ListLayout();
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                    ?? throw new JsonException("設定檔內容是 null");
                settings.Normalize();
                return settings;
            }
        }
        catch
        {
            string? backup = BrokenFile.Preserve(FilePath);
            if (backup is null)
            {
                return new AppSettings
                {
                    SaveDisabled = true,
                    LoadProblem = "設定檔 GVODiscoverySync.json 讀不出來，可能被其他程式鎖住了。\n\n" +
                                  "這次先用預設值，而且不會儲存設定。請關掉程式，確認檔案沒被占用後再開一次。",
                };
            }
            return new AppSettings
            {
                LoadProblem = "設定檔 GVODiscoverySync.json 讀不出來，可能已經損壞。\n\n" +
                              $"原本的檔案已改名保留為：\n{Path.GetFileName(backup)}\n\n設定已恢復成預設值。",
            };
        }
        return new AppSettings();
    }

    public void Save()
    {
        if (SaveDisabled)
        {
            return;
        }
        try
        {
            JsonSerializerOptions options = new() { WriteIndented = true };
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, options));
        }
        catch (Exception ex)
        {
            // 存不了（例如放在唯讀資料夾）不影響使用
            System.Diagnostics.Debug.WriteLine("GVODiscoverySync.json 存檔失敗：" + ex);
        }
    }
}
