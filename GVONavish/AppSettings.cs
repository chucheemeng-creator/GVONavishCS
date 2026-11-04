// AppSettings.cs
// 設定檔，存在程式同一個資料夾的 settings.json。
// 取代原版的 GVONavish.ini。

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GVO.Common.IO;
using GVONavish.Core;
using GVONavish.UI;

namespace GVONavish;

public class AppSettings
{
    // ── 地圖 ──
    /// <summary>地圖圖檔路徑。可以是相對於程式資料夾的路徑，或完整路徑。</summary>
    public string MapFile { get; set; } = "map.png";

    /// <summary>顯示比例（1.0 = 100%）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>地圖是否跟隨船的位置。</summary>
    public bool FollowShip { get; set; } = true;

    /// <summary>是否顯示航向線。</summary>
    public bool ShowHeadingLine { get; set; } = true;

    /// <summary>是否顯示船速。</summary>
    public bool ShowSpeed { get; set; } = true;

    /// <summary>是否顯示航跡。</summary>
    public bool ShowRoutes { get; set; } = true;

    /// <summary>船速框的位置（0 右上、1 右下、2 左上、3 左下）。</summary>
    public ScreenCorner SpeedCorner { get; set; } = ScreenCorner.TopRight;

    /// <summary>最後一次的船位置（下次開啟時先顯示這裡）。預設值和原版相同。</summary>
    public int LastX { get; set; } = 15785;
    public int LastY { get; set; } = 3204;

    /// <summary>上一次規劃航線的起點、終點（港口名稱或座標文字）。</summary>
    public string PlanStart { get; set; } = "";
    public string PlanEnd { get; set; } = "";

    /// <summary>最近用過的港口名稱（新的在前，最多 10 個），規劃航線的候選清單會排在最上面。</summary>
    public System.Collections.Generic.List<string> RecentPorts { get; set; } = [];

    // ── 視窗 ──
    /// <summary>視窗位置和大小。寬度為 0 表示還沒存過，用預設值。</summary>
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool TopMost { get; set; }

    /// <summary>
    /// 外觀："Classic"（淺色，預設）、"Dark"（深色）、"System"（跟隨 Windows 的設定）。
    /// 程式啟動時套用，改了要重新開程式。
    /// </summary>
    public string ColorMode { get; set; } = "Classic";

    /// <summary>「航線管理」視窗的位置和大小。寬度為 0 表示還沒存過（開在主視窗旁邊）。</summary>
    public int RouteManagerX { get; set; }
    public int RouteManagerY { get; set; }
    public int RouteManagerWidth { get; set; }
    public int RouteManagerHeight { get; set; }

    /// <summary>「航線管理」清單每一欄的寬度（像素）。空的表示用預設寬度。</summary>
    public System.Collections.Generic.List<int> RouteManagerColumns { get; set; } = [];

    // ── 讀座標 ──
    /// <summary>截圖位置：距離遊戲畫面右下角的 X、Y。原版是 70、273。</summary>
    public int OffsetX { get; set; } = 70;
    public int OffsetY { get; set; } = 273;

    /// <summary>讀座標的間隔（毫秒）。</summary>
    public int IntervalMs { get; set; } = 500;

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>地圖圖檔的完整路徑。</summary>
    public string GetMapFullPath()
    {
        if (MapFile == "")
        {
            return "";
        }
        return Path.IsPathRooted(MapFile) ? MapFile : Path.Combine(AppContext.BaseDirectory, MapFile);
    }

    /// <summary>
    /// 讀不出來、原檔又無法改名保留時是 true：這次不存檔，免得用預設值蓋掉原本的設定。
    /// </summary>
    [JsonIgnore]
    public bool SaveDisabled { get; private set; }

    /// <summary>讀檔時發生的問題（給使用者看的說明）；沒問題是 null。主視窗出現後會顯示。</summary>
    [JsonIgnore]
    public string? LoadProblem { get; private set; }

    /// <summary>
    /// 讀進來之後補正：JSON 裡寫成 null 的欄位不會讓讀檔失敗，卻會在之後用到時出錯（例如 Path.IsPathRooted(null)），
    /// 所以在這裡一次換成預設值。
    /// </summary>
    private void Normalize()
    {
        MapFile ??= "map.png";
        PlanStart ??= "";
        PlanEnd ??= "";
        RecentPorts ??= [];
        RecentPorts.RemoveAll(p => p is null);
        RouteManagerColumns ??= [];
        if (ColorMode is not ("Classic" or "Dark" or "System"))
        {
            ColorMode = "Classic";
        }
        if (RecentPorts.Count > 10)
        {
            RecentPorts.RemoveRange(10, RecentPorts.Count - 10);
        }

        // 數值：和設定畫面能選的範圍一致（手動改設定檔時，可能填了不合理的值）
        OffsetX = Math.Clamp(OffsetX, 0, 5000);
        OffsetY = Math.Clamp(OffsetY, 0, 5000);
        IntervalMs = Math.Clamp(IntervalMs, 100, 5000);
        if (double.IsNaN(Scale) || double.IsInfinity(Scale) || Scale <= 0)
        {
            Scale = 1.0; // 範圍由地圖元件再限制（12.5%～400%）
        }
        if (!Enum.IsDefined(SpeedCorner))
        {
            SpeedCorner = ScreenCorner.TopRight;
        }
        if (!World.IsValid(new WorldPoint(LastX, LastY)))
        {
            LastX = 15785; // 預設：里斯本附近
            LastY = 3204;
        }
    }

    /// <summary>上一次存檔失敗了（例如程式放在唯讀資料夾、磁碟滿了）。下一次存成功就會清掉。</summary>
    [JsonIgnore]
    public bool LastSaveFailed { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // 檔案內容是 null（合法的 JSON）也當成壞掉：要走下面的「改名保留」，不能悄悄用預設值
                AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                    ?? throw new JsonException("設定檔內容是 null");
                settings.Normalize();
                return settings;
            }
        }
        catch
        {
            // 設定檔壞掉就用預設值。原檔改名保留（settings.json.broken-日期時間），需要時還能手動救回；
            // 改名失敗（例如被鎖住）就不存檔，免得蓋掉
            string? backup = BrokenFile.Preserve(FilePath);
            if (backup is null)
            {
                return new AppSettings
                {
                    SaveDisabled = true,
                    LoadProblem = "設定檔 settings.json 讀不出來，可能被其他程式鎖住了。\n\n" +
                                  "這次先用預設值（視窗位置、讀座標位置等）。為了不蓋掉原本的檔案，這次執行期間不會儲存設定。\n" +
                                  "請關掉程式，確認檔案沒被占用後再開一次。",
                };
            }
            return new AppSettings
            {
                LoadProblem = "設定檔 settings.json 讀不出來，可能已經損壞。\n\n" +
                              $"原本的檔案已改名保留為：\n{Path.GetFileName(backup)}\n\n" +
                              "設定已恢復成預設值（視窗位置、讀座標位置、顯示選項等），需要的話請重新設定。",
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
            // 先寫到暫存檔再換掉原檔（和 routes.json 一樣）：
            // 每 30 秒會存一次位置，寫到一半斷電或強制結束時，settings.json 才不會變成空檔
            JsonSerializerOptions options = new() { WriteIndented = true };
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, options));
            LastSaveFailed = false;
        }
        catch (Exception ex)
        {
            // 存不了（例如放在唯讀資料夾）不影響使用：原檔不動，記錄下來，主視窗的狀態列會提示
            System.Diagnostics.Debug.WriteLine("settings.json 存檔失敗：" + ex);
            LastSaveFailed = true;
        }
    }
}
