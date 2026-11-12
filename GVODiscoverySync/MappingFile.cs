// MappingFile.cs
// 使用者可以自己改的對照表（JSON，放在程式同一個資料夾）：
//   - 錯字對照表 GVODiscoverySync.OcrConfusions.json：文字辨識固定會讀錯的字，讀到的 → 正確的
//   - 名稱對照表 GVODiscoverySync.NameAliases.json：Excel 和遊戲寫法不同的名稱，Excel 的寫法 → 遊戲上的寫法
//
// 和設定檔（GVODiscoverySync.json）分開：設定檔在關閉程式時會整個重寫（視窗位置等），
// 對照表卻常常是程式開著、看到讀錯的字時順手修改，放在一起的話，改好的內容會在關閉程式時被蓋掉。
// 檔案程式只在第一次（檔案還不存在）時建立，之後只讀不寫；
// 執行中修改、存檔後，下一次讀取畫面時就會自動換成新的對照表。
// 內建的對照沒寫在檔案裡也會使用，所以更新程式後新加的內建對照不用手動加。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using GVO.Common.IO;

namespace GVODiscoverySync;

public sealed class MappingFile
{
    /// <summary>
    /// 內建的錯字對照表（讀到的 → 正確的）。檔案裡沒寫的也會使用；
    /// 不想用某一組內建的，在檔案裡把它改成對應到自己（例如 "專": "專"）。
    /// </summary>
    public static readonly Dictionary<string, string> BuiltInConfusions = new()
    {
        ["巑"] = "鑽", // 黃鑽、粉鑽、藍鑽、科依諾爾鑽
        ["專"] = "轉", // 汽轉球 → 專球（漏了偏旁）
        ["降"] = "隆", // 隆派亞長刀 → 降派亞
        ["冫每"] = "海", // 「氵」讀成「冫」：海埔姜 → 冫每埔姜、海洋生物化石 → 冫每洋生物化石
        ["冫少"] = "沙", // 沙漠螞蟻 → 冫少漠螞蟻
        ["冫魚"] = "漁", // 漁貓、大漁場：「氵」讀成「冫」
        ["/魚"] = "漁", // 同上，「氵」讀成「/」
        ["采彡"] = "彩", // 「彩」拆成兩個字（施彩樹皮的馬具、彩色壁畫的破片）
        ["鴡"] = "鴞", // 雪鴞、倉鴞
        ["猷"] = "獸", // 石獸、鳥獸人物畫
        ["睡"] = "唾", // 唾蛇（睡蓮等原本就有「睡」的名稱不受影響）
        ["鼰"] = "鼴", // 荒漠鼴、針鼴
        ["慧"] = "彗", // 哈雷彗星、恩克彗星
        ["甁"] = "瓶", // 香水瓶、玻璃瓶
        ["圑"] = "團", // 騎士團
        ["P"] = "β", // 天鵝座β
        ["ß"] = "β", // 同上（讀成德文的 ß）
        ["嚳"] = "曇", // 曇花
        ["瞿"] = "鷺", // 白鷺花、黑面琵鷺
        ["蟃"] = "鰻", // 電鰻、擬鰻鮫
        ["鳗"] = "鰻", // 同上（簡體字形）
        ["鲽"] = "鰈", // 鰈魚、狹鱗庸鰈（簡體字形）
        ["蟤"] = "螺", // 海螺珍珠、螺旋型叫拜樓
        ["甫"] = "埔", // 海埔姜、北埔蟬：「土」字旁沒讀到
        ["瑡"] = "獅", // 石獅子、鬆獅犬、獅心王的甲冑等：「犭」讀成「王」
        ["矕"] = "彎", // 藍彎角羚、彎角大羚羊、福特彎圖拉島
        ["令"] = "羚", // 高角羚、湯氏瞪羚等：「羊」字旁沒讀到（法令的木簡、白令海峽原本就有「令」，不受影響）
        ["旨"] = "脂", // 金脂鯉、黑脂鯧：「月」字旁沒讀到
        ["橵"] = "撒", // 撒哈拉沙漠、凱撒大帝的寶劍等
    };

    /// <summary>
    /// 內建的名稱對照表（Excel 的寫法 → 遊戲上的寫法）：Excel 為了和別的分類區分加了註記、遊戲上沒有的。
    /// </summary>
    public static readonly Dictionary<string, string> BuiltInNameAliases = new()
    {
        ["關島(港)"] = "關島", // 地理類也有「關島」，港口類的 Excel 寫成「關島(港)」，遊戲上都是「關島」
    };

    private static readonly string[] ConfusionsHeader =
    [
        "文字辨識固定會讀錯的字：左邊是讀到的，右邊是正確的。",
        "比對時照原樣、換字後各比一次，取比較像的，所以原本就有這個字的名稱不受影響。",
        "程式開著也可以改，存檔後下一次讀取畫面時就會用新的對照表。",
        "程式內建的錯字（巑→鑽 等）沒寫在這裡也會使用；不想用某一組內建的，把它改成對應到自己（例如 \"專\": \"專\"）。",
        "可以寫 // 註解，最後一行後面多一個逗號也沒關係。",
    ];

    private static readonly string[] NameAliasesHeader =
    [
        "Excel 和遊戲寫法不同的名稱：左邊是 Excel 上的寫法，右邊是遊戲畫面上的寫法。",
        "Excel 為了和別的分類區分加了註記、遊戲上沒有時用（例如地理類也有「關島」，港口類的 Excel 寫成「關島(港)」）。",
        "左邊要和 Excel 一字不差（括號的全形、半形也要一樣），寫錯不會有提示，只是沒有作用。",
        "程式開著也可以改，存檔後下一次讀取畫面時就會套用。",
        "程式內建的（關島(港) 等）沒寫在這裡也會使用；不想用某一組內建的，把它改成對應到自己。",
        "可以寫 // 註解，最後一行後面多一個逗號也沒關係。",
    ];

    /// <summary>這個對照表的檔案（完整路徑）。</summary>
    public string FilePath { get; }

    private readonly IReadOnlyDictionary<string, string> _builtIn;
    private readonly string _title; // 訊息用的名稱：錯字對照表、名稱對照表
    private readonly string[] _header;

    private MappingFile(string fileName, IReadOnlyDictionary<string, string> builtIn, string title, string[] header)
    {
        FilePath = Path.Combine(AppContext.BaseDirectory, fileName);
        _builtIn = builtIn;
        _title = title;
        _header = header;
        Map = new(builtIn);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>目前使用的對照表（檔案的內容加上檔案裡沒寫的內建對照）。</summary>
    public Dictionary<string, string> Map { get; private set; }

    // 上一次讀檔時檔案的修改時間（檔案不存在時是 DateTime.MinValue）
    private DateTime _lastWrite = DateTime.MinValue;

    /// <summary>
    /// 讀取錯字對照表。檔案還不存在時建立一份：內容是內建的錯字，加上舊版設定檔裡的（oldSettings，搬過來）。
    /// 回傳的 Migrated 表示舊設定檔的內容已經安全寫進新檔案，可以從設定檔拿掉了。
    /// </summary>
    public static (MappingFile Table, bool Migrated, string? Problem) LoadConfusions(IReadOnlyDictionary<string, string>? oldSettings)
        => new MappingFile("GVODiscoverySync.OcrConfusions.json", BuiltInConfusions, "錯字對照表", ConfusionsHeader).Load(oldSettings);

    /// <summary>讀取名稱對照表（Excel 的寫法 → 遊戲上的寫法）。檔案還不存在時建立一份，內容是內建的。</summary>
    public static (MappingFile Table, string? Problem) LoadNameAliases()
    {
        (MappingFile table, _, string? problem) =
            new MappingFile("GVODiscoverySync.NameAliases.json", BuiltInNameAliases, "名稱對照表", NameAliasesHeader).Load(null);
        return (table, problem);
    }

    private (MappingFile Table, bool Migrated, string? Problem) Load(IReadOnlyDictionary<string, string>? oldSettings)
    {
        MappingFile table = this;
        bool migrated = false;
        if (!File.Exists(FilePath))
        {
            Dictionary<string, string> initial = new(_builtIn);
            foreach ((string wrong, string right) in oldSettings ?? new Dictionary<string, string>())
            {
                initial[wrong] = right; // 自己加的、改過的優先
            }
            try
            {
                AtomicFile.WriteAllText(FilePath, Template(initial));
                migrated = true;
            }
            catch
            {
                // 存不了（例如放在唯讀資料夾）：這次直接用記憶體裡的，舊設定檔的內容也先留著
                table.Map = initial;
                return (table, false, null);
            }
        }
        else
        {
            migrated = true; // 新檔案已經有了，舊設定檔裡的不再使用
        }
        string? problem = table.Reload();
        return (table, migrated, problem);
    }

    /// <summary>
    /// 檔案改過（修改時間不同）就重新讀取。回傳 null 表示沒有改；
    /// 有改時回傳要顯示的訊息（讀取成功或失敗）。讀取失敗時繼續用原本的對照表。
    /// </summary>
    public string? ReloadIfChanged()
    {
        DateTime write;
        try
        {
            write = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        }
        catch
        {
            return null;
        }
        if (write == _lastWrite)
        {
            return null;
        }
        return Reload() ?? $"{_title}已更新（{Map.Count} 組），這一頁重新比對。";
    }

    // 讀檔，成功回傳 null，失敗回傳說明（Map 不變）
    private string? Reload()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                _lastWrite = DateTime.MinValue;
                Map = new(_builtIn);
                return null;
            }
            DateTime write = File.GetLastWriteTimeUtc(FilePath);
            string text = File.ReadAllText(FilePath);
            Dictionary<string, string> map = JsonSerializer.Deserialize<Dictionary<string, string>>(text, JsonOptions) ?? [];
            foreach ((string from, string to) in _builtIn)
            {
                map.TryAdd(from, to);
            }
            Map = map;
            _lastWrite = write;
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 記下這次的修改時間，同一個錯誤不要每次讀畫面都再報一次；改好存檔後修改時間變了會再讀
            try
            {
                _lastWrite = File.GetLastWriteTimeUtc(FilePath);
            }
            catch
            {
                // 讀不到修改時間：下次再試
            }
            return ex is JsonException
                ? $"{_title} {Path.GetFileName(FilePath)} 格式有錯，先繼續用原本的對照表：{ex.Message}"
                : $"{_title} {Path.GetFileName(FilePath)} 讀不出來（可能正在存檔），先繼續用原本的對照表。";
        }
    }

    // 第一次建立的檔案內容：前面是說明（// 註解），每一組一行
    private string Template(Dictionary<string, string> map)
    {
        StringBuilder sb = new();
        foreach (string line in _header)
        {
            sb.Append("// ").AppendLine(line);
        }
        sb.AppendLine("{");
        foreach ((string from, string to) in map)
        {
            sb.Append("  ")
              .Append(JsonSerializer.Serialize(from, JsonOptions))
              .Append(": ")
              .Append(JsonSerializer.Serialize(to, JsonOptions))
              .AppendLine(",");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }
}
